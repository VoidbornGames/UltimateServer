using HttpMultipartParser;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Server.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Servers
{
    public class HttpServer
    {
        private readonly int _port;
        private readonly string _ip;
        private readonly Logger _logger;
        private readonly UserService _userService;
        private readonly AuthenticationService _authService;
        private readonly VideoService _videoService;
        private readonly CompressionService _compressionService;
        private readonly PluginManager _pluginManager;
        private readonly PackageManager _packageManager;
        private readonly ConfigManager _configManager;
        private readonly DownloadJobProcessor _downloadJobProcessor;
        private readonly DDoSProtectionService _ddosProtection;
        private readonly FeatherDatabase _featherDatabase;
        private readonly IServiceProvider _serviceProvider;
        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private double _lastCpuUsage = 0;
        private readonly ConcurrentDictionary<TcpClient, bool> _activeClients = new();
        private bool useCompression = false;
        private string panelWebPage;
        private long activeConnections;
        private long activePlugins;

        public HttpServer(
            ServerSettings settings,
            Logger logger,
            UserService userService,
            AuthenticationService authService,
            VideoService videoService,
            PluginManager pluginManager,
            IServiceProvider serviceProvider,
            ConfigManager configManager,
            PackageManager packageManager,
            FeatherDatabase featherDatabase)
        {
            _port = settings.httpPort;
            _ip = settings.Ip;
            _logger = logger;
            _userService = userService;
            _logger.Log($"🌐 HttpServer initialized for port {_port}.");
            _authService = authService;
            _videoService = videoService;
            _pluginManager = pluginManager;
            _serviceProvider = serviceProvider;
            _compressionService = new CompressionService(new ServerConfig(), logger);
            _cts = new CancellationTokenSource();
            _configManager = configManager;
            useCompression = configManager.Config.EnableCompression;
            _downloadJobProcessor = new DownloadJobProcessor(_logger, _pluginManager, packageManager);
            _packageManager = packageManager;
            _featherDatabase = featherDatabase;

            _ddosProtection = new DDoSProtectionService(
                logger,
                maxRequestsPerMinute: 240,
                maxConcurrentConnections: 3,
                maxRequestSizeKB: configManager.Config.MaxRequestSizeMB * 1024,
                maxHeaderSizeKB: configManager.Config.MaxRequestSizeMB * 2,
                blockMinutesBase: 3
            );
        }

        public async Task Start()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    string htmlPath = Path.Combine(AppContext.BaseDirectory, "index.html");
                    string cssPath = Path.Combine(AppContext.BaseDirectory, "style.css");
                    string jsPath = Path.Combine(AppContext.BaseDirectory, "script.js");

                    if (File.Exists(htmlPath))
                    {
                        string webPage = await File.ReadAllTextAsync(htmlPath);

                        if (File.Exists(cssPath))
                        {
                            string css = await File.ReadAllTextAsync(cssPath);
                            string styledHtml = webPage.Replace("</head>", $"<style>\n{css}\n</style>\n</head>");
                            webPage = styledHtml;
                        }
                        if (File.Exists(jsPath))
                        {
                            string js = await File.ReadAllTextAsync(jsPath);
                            string funcedHtml = webPage.Replace("</body>", $"<script>\n{js}\n</script>\n</body>");
                            webPage = funcedHtml;
                        }
                        panelWebPage = webPage;
                    }
                    await Task.Delay(5000);
                }
            });

            try
            {
                _httpListener = new HttpListener();
                string prefix = $"http://*:{_port}/";
                _httpListener.Prefixes.Add(prefix);
                _httpListener.IgnoreWriteExceptions = true;

                _httpListener.Start();
                _logger.Log($"🌐 HTTP server listening on {prefix}");

                activeConnections = 0;
                var maxConnections = _configManager.Config.MaxConnections;

                _ = Task.Run(async () =>
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            if (activeConnections >= maxConnections)
                            {
                                await Task.Delay(100, _cts.Token);
                                continue;
                            }

                            var context = await _httpListener.GetContextAsync();
                            Interlocked.Increment(ref activeConnections);

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await HandleRequestAsync(context);
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref activeConnections);
                                }
                            }).ContinueWith(t =>
                            {
                                if (t.Exception != null)
                                    _logger.LogError($"Request handling error: {t.Exception.Message}");
                            });
                        }
                        catch (Exception ex) when (_cts.Token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Listener error: {ex.Message}");
                        }
                    }
                }, _cts.Token);

                _ = Task.Run(async () =>
                {
                    var proc = Process.GetCurrentProcess();
                    TimeSpan prevCpu = proc.TotalProcessorTime;
                    DateTime prevTime = DateTime.UtcNow;

                    while (!_cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000);
                        TimeSpan currCpu = proc.TotalProcessorTime;
                        DateTime currTime = DateTime.UtcNow;

                        double cpuUsedMs = (currCpu - prevCpu).TotalMilliseconds;
                        double elapsedMs = (currTime - prevTime).TotalMilliseconds;
                        int cores = Environment.ProcessorCount;

                        _lastCpuUsage = Math.Round(cpuUsedMs / (elapsedMs * cores) * 100, 2);

                        prevCpu = currCpu;
                        prevTime = currTime;
                    }
                });
            }
            catch (HttpListenerException ex)
            {
                _logger.LogError($"❌ Failed to start HTTP server: {ex.Message}");
                _logger.LogError($"💡 Tip: Make sure port {_port} is not already in use");
                throw;
            }
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _httpListener?.Stop();
            _logger.Log("🌐 HTTP server stopped.");

            await _downloadJobProcessor.StopAsync();
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            bool requestHandled = false;

            response.AddHeader("Access-Control-Allow-Origin", "*");
            if (request.HttpMethod == "OPTIONS")
            {
                response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                response.StatusCode = 200;
                response.Close();
                return;
            }

            if (!_ddosProtection.IsAllowed(request))
            {
                response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                await WriteJsonResponseAsync(response, new
                {
                    success = false,
                    message = "Request blocked due to rate limiting or security policy"
                });
                response.Close();
                return;
            }

            HttpContextHolder.CurrentResponse = response;
            try
            {
                foreach (var pluginEntry in _pluginManager.GetLoadedPlugins())
                {
                    var plugin = pluginEntry.Value;
                    var pluginCtx = _pluginManager.GetPluginContext(plugin.Name);
                    if (pluginCtx != null)
                    {
                        var handler = pluginCtx.GetRouteHandler(request.Url.AbsolutePath);
                        if (handler != null)
                        {
                            try
                            {
                                await handler(request);
                                requestHandled = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Plugin route handler error: {ex.Message}");
                                requestHandled = true;
                                break;
                            }
                        }
                    }
                }

                if (requestHandled)
                {
                    return;
                }

                switch (request.Url.AbsolutePath)
                {
                    case "/api/login":
                        await HandleLoginAsync(request, response);
                        break;

                    case "/api/register":
                        await HandleRegisterAsync(request, response);
                        break;

                    case "/api/verify-2fa":
                        await HandleTwoFactorVerificationAsync(request, response);
                        break;

                    case "/api/plugins":
                        if (ValidateAdminAuthentication(request))
                            await HandlePluginsListAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/plugins/upload":
                        if (ValidateAdminAuthentication(request))
                            await HandlePluginUploadAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/plugins/reload":
                        if (ValidateAdminAuthentication(request))
                            await HandlePluginsReloadAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/stats":
                        if (ValidateAdminAuthentication(request))
                            await HandleStatsAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/system":
                        if (ValidateAdminAuthentication(request))
                            await HandleSystemAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/logs":
                        if (ValidateAdminAuthentication(request))
                            await HandleLogsAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/videos":
                        if (ValidateUserAuthentication(request))
                            await HandleVideosAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/upload-url":
                        if (ValidateAdminAuthentication(request))
                            await HandleVideoUploadAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;


                    case "/api/request-password-reset":
                        await HandleRequestPasswordResetAsync(request, response);
                        break;

                    case "/api/confirm-password-reset":
                        await HandleConfirmPasswordResetAsync(request, response);
                        break;

                    case "/api/sites":
                        if (ValidateAdminAuthentication(request))
                            await HandleRequestSitesAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/marketplace":
                        if (ValidateAdminAuthentication(request))
                            await HandleMarketAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/marketplace/download":
                        if (ValidateAdminAuthentication(request))
                            await HandleMarketDownloadAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/process/list":
                        if (ValidateAdminAuthentication(request))
                            await HandleProcessListAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/process/kill":
                        if (ValidateAdminAuthentication(request))
                            await HandleProcessKill(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    default:
                        if (request.Url.AbsolutePath.StartsWith("/api/plugins/") &&
                            (request.Url.AbsolutePath.EndsWith("/enable") || request.Url.AbsolutePath.EndsWith("/disable")))
                        {
                            if (ValidateAdminAuthentication(request))
                            {
                                var pathParts = request.Url.AbsolutePath.Split('/');
                                if (pathParts.Length >= 4)
                                {
                                    var pluginId = pathParts[3];
                                    var enable = request.Url.AbsolutePath.EndsWith("/enable");
                                    await HandlePluginToggleAsync(request, response, pluginId, enable);
                                }
                                else
                                {
                                    response.StatusCode = 400;
                                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid plugin ID" });
                                }
                            }
                            else
                                SendUnauthorized(response);
                        }
                        else if (request.Url.AbsolutePath.StartsWith("/videos/"))
                        {
                            if (ValidateAdminAuthentication(request))
                                await ServeVideoAsync(request, response);
                            else
                                SendUnauthorized(response);
                        }
                        else
                        {
                            await ServeDefaultPageAsync(request, response);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"HTTP server error: {ex.Message}");
                response.StatusCode = 500;
            }
            finally
            {
                _ddosProtection.OnRequestFinished(request);

                HttpContextHolder.CurrentResponse = null;

                if (!requestHandled)
                {
                    response.Close();
                }
            }
        }


        private async Task HandleRequestSitesAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod == "GET")
            {
                try
                {
                    var sites = new Dictionary<string, int>();

                    foreach(var site in _serviceProvider.GetRequiredService<SitePress>().sites)
                        sites.Add(site.Key, site.Value);

                    await WriteJsonResponseAsync(response, sites);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error fetching sites: {ex.Message}");
                    response.StatusCode = 500;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
                }
            }
            else if (request.HttpMethod == "POST")
            {
                try
                {
                    using var reader = new StreamReader(request.InputStream);
                    string body = await reader.ReadToEndAsync();
                    var data = JsonConvert.DeserializeObject<CreateSiteRequest>(body);

                    if (data != null && data.Port >= 1000 && data.Port <= 50000 && !string.IsNullOrEmpty(data.Name))
                    {
                        bool success;
                        string message;

                        if (_serviceProvider.GetRequiredService<SitePress>().sites.TryGetValue(data.Name, out _))
                            (success, message) = (false, "Failed to create site - already exist");

                        if (await _serviceProvider.GetRequiredService<SitePress>().CreateSite(data.Name, data.Port))
                            (success, message) = (true, "Site created successfully");
                        else
                            (success, message) = (false, "Failed to create site - port may be in use");

                        await WriteJsonResponseAsync(response, new { success, message });
                    }
                    else
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponseAsync(response, new { success = false, message = "Name and Port are required" });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Site creation error: {ex.Message}");
                    response.StatusCode = 500;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
                }
            }
            else if (request.HttpMethod == "DELETE")
            {
                try
                {
                    using var reader = new StreamReader(request.InputStream);
                    string body = await reader.ReadToEndAsync();
                    var data = JsonConvert.DeserializeObject<DeleteSiteRequest>(body);

                    if (data != null && !string.IsNullOrEmpty(data.Name))
                    {
                        bool success;
                        string message;

                        if (await _serviceProvider.GetRequiredService<SitePress>().DeleteSite(data.Name))
                            (success, message) = (true, "Site deleted successfully");
                        else
                            (success, message) = (false, "Site not found");

                        await WriteJsonResponseAsync(response, new { success, message });
                    }
                    else
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponseAsync(response, new { success = false, message = "Site name is required" });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Site deletion error: {ex.Message}");
                    response.StatusCode = 500;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
                }
            }
            else
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Method not allowed" });
                return;
            }
        }

        private async Task HandleRequestPasswordResetAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                if (data != null && data.TryGetValue("email", out var email))
                {
                    var (success, message) = await _userService.ResetPasswordAsync(email);
                    await WriteJsonResponseAsync(response, new { success, message });
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Email is required" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Password reset request error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
            }
        }

        private async Task HandleConfirmPasswordResetAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }
            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var resetRequest = JsonConvert.DeserializeObject<ChangePasswordRequest>(body);
                if (resetRequest != null)
                {
                    if (string.IsNullOrEmpty(resetRequest.Email) || string.IsNullOrEmpty(resetRequest.Token) || string.IsNullOrEmpty(resetRequest.NewPassword))
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponseAsync(response, new { success = false, message = "Email, token, and new password are required." });
                        return;
                    }
                    var (success, message) = await _userService.ConfirmPasswordResetAsync(resetRequest);
                    await WriteJsonResponseAsync(response, new { success, message });
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid request data" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Password reset confirmation error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
            }
        }

        private async Task HandleLoginAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(body);

                if (loginRequest != null)
                {
                    var (user, message) = await _userService.AuthenticateUserAsync(loginRequest);
                    if (user != null)
                    {
                        var token = _authService.GenerateJwtToken(user);
                        var responseData = new
                        {
                            success = true,
                            token,
                            refreshToken = user.RefreshToken,
                            user = new { username = user.Username, role = user.Role, email = user.Email }
                        };
                        await WriteJsonResponseAsync(response, responseData);
                        _logger.Log($"✅ User logged in: {user.Username}");
                    }
                    else
                    {
                        response.StatusCode = 401;
                        await WriteJsonResponseAsync(response, new { success = false, message });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid request" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Login error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
            }
        }

        private async Task HandleRegisterAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(body);

                if (registerRequest != null)
                {
                    var (user, message) = await _userService.CreateUserAsync(registerRequest);
                    if (user != null && user.Role.ToLower() == "admin")
                    {
                        var token = _authService.GenerateJwtToken(user);
                        var responseData = new
                        {
                            success = true,
                            token,
                            refreshToken = user.RefreshToken,
                            user = new { username = user.Username, role = user.Role, email = user.Email }
                        };
                        await WriteJsonResponseAsync(response, responseData);
                        _logger.Log($"✅ User registered: {user.Username}");
                    }
                    else
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponseAsync(response, new { success = false, message });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid request" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Registration error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
            }
        }

        private async Task HandleTwoFactorVerificationAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            await WriteJsonResponseAsync(response, new { success = false, message = "2FA not implemented" });
        }


        private async Task HandlePluginsListAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                var plugins = _pluginManager.GetLoadedPlugins();
                var pluginList = plugins.Select(p => new
                {
                    id = p.Key,
                    name = p.Value.Name,
                    version = p.Value.Version,
                    enabled = true
                }).ToList();

                await WriteJsonResponseAsync(response, new { success = true, plugins = pluginList });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error listing plugins: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to list plugins" });
            }
        }

        /// <summary>
        /// Handles the upload of a plugin file using a safe, temporary-file process.
        /// </summary>
        private async Task HandlePluginUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            if (!request.HasEntityBody)
            {
                response.StatusCode = 400;
                await WriteJsonResponseAsync(response, new { success = false, message = "No file uploaded" });
                return;
            }

            var contentType = request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("multipart/form-data"))
            {
                response.StatusCode = 400;
                await WriteJsonResponseAsync(response, new { success = false, message = "Invalid content type" });
                return;
            }

            string pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
            if (!Directory.Exists(pluginsDirectory))
                Directory.CreateDirectory(pluginsDirectory);

            string tempFilePath = null;
            string finalFilePath = null;

            try
            {
                _logger.Log("📤 Receiving plugin file...");
                var (filename, fileContent) = await ReadMultipartFileAsync(request);
                if (string.IsNullOrEmpty(filename))
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "No filename found in upload." });
                    return;
                }

                tempFilePath = Path.Combine(pluginsDirectory, $"{filename}.tmp");
                finalFilePath = Path.Combine(pluginsDirectory, filename);
                if(Path.GetExtension(filename) == ".dll")
                {
                    await File.WriteAllBytesAsync(tempFilePath, fileContent);
                    _logger.Log($"✅ Plugin temporarily saved to '{tempFilePath}'");

                    _logger.Log("🔄 Unloading existing plugins before replacement...");
                    await _pluginManager.UnloadAllPluginsAsync();

                    if (File.Exists(finalFilePath))
                    {
                        File.Delete(finalFilePath);
                    }
                    File.Move(tempFilePath, finalFilePath);
                    _logger.Log($"✅ Replaced old plugin with '{finalFilePath}'");
                    tempFilePath = null;

                    await _pluginManager.LoadPluginsAsync();
                }
                else if(Path.GetExtension(filename) == ".pkg")
                {
                    var package = await _packageManager.GetPackageFromByteArray(fileContent);
                    if (package != null)
                        await _packageManager.InstallPackage(package);
                    else
                    {
                        await WriteJsonResponseAsync(response, new { success = false, message = "Invalid content type" });
                        return;
                    }
                }

                _logger.Log("✅ Plugin upload and reload complete.");
                await WriteJsonResponseAsync(response, new { success = true, message = "Plugin uploaded and reloaded successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading plugin: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to upload plugin." });
            }
            finally
            {
                if (tempFilePath != null && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        _logger.Log($"🧹 Cleaned up temporary file '{tempFilePath}'");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to clean up temporary file: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Parses multipart/form-data to extract a single file.
        /// </summary>
        private async Task<(string filename, byte[] data)> ReadMultipartFileAsync(HttpListenerRequest request)
        {
            var contentType = request.ContentType;
            var boundary = contentType.Split(';')[1].Split('=')[1].Trim('"');
            var boundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}");
            var endBoundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}--");

            using var memoryStream = new MemoryStream();
            await request.InputStream.CopyToAsync(memoryStream);
            var requestData = memoryStream.ToArray();

            int startIndex = FindIndexOf(requestData, boundaryBytes);
            if (startIndex == -1) return (null, null);
            startIndex += boundaryBytes.Length;

            int headerEndIndex = FindIndexOf(requestData, new byte[] { 0x0D, 0x0A, 0x0D, 0x0A }, startIndex);
            if (headerEndIndex == -1) return (null, null);
            int fileContentStartIndex = headerEndIndex + 4;

            int endIndex = FindIndexOf(requestData, endBoundaryBytes, fileContentStartIndex);
            if (endIndex == -1) return (null, null);

            var headerBytes = new byte[headerEndIndex - startIndex];
            Array.Copy(requestData, startIndex, headerBytes, 0, headerBytes.Length);
            var headerText = Encoding.UTF8.GetString(headerBytes);
            var filenameMatch = System.Text.RegularExpressions.Regex.Match(headerText, @"filename=""([^""]+)""");
            var filename = filenameMatch.Success ? filenameMatch.Groups[1].Value : null;

            var fileContentLength = endIndex - fileContentStartIndex;
            var fileContent = new byte[fileContentLength];
            Array.Copy(requestData, fileContentStartIndex, fileContent, 0, fileContentLength);

            return (filename, fileContent);
        }

        /// <summary>
        /// Finds a byte array within another byte array.
        /// </summary>
        private int FindIndexOf(byte[] source, byte[] pattern, int startIndex = 0)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
            {
                if (source[i] == pattern[0])
                {
                    bool found = true;
                    for (int j = 1; j < pattern.Length; j++)
                    {
                        if (source[i + j] != pattern[j])
                        {
                            found = false;
                            break;
                        }
                    }
                    if (found) return i;
                }
            }
            return -1;
        }

        private async Task HandlePluginsReloadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                _logger.Log("🔄 Reloading plugins via API request...");
                var pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");

                await _pluginManager.UnloadAllPluginsAsync();

                _logger.Log("⏳ Waiting for file locks to be released...");
                await Task.Delay(1000);

                await _pluginManager.LoadPluginsAsync();

                _logger.Log("✅ Plugin reload complete.");
                await WriteJsonResponseAsync(response, new { success = true, message = "Plugins reloaded successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reloading plugins: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to reload plugins." });
            }
        }

        private async Task HandleMarketAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only GET allowed" });
                return;
            }

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var marketResponse = await client.GetAsync("https://dashboard.voidgames.ir/api/market/plugins");
                    marketResponse.EnsureSuccessStatusCode();

                    var content = await marketResponse.Content.ReadAsStringAsync();
                    var marketPlugins = JsonConvert.DeserializeObject<List<MarketPlugin>>(content);

                    foreach (var plugin in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "plugins")))
                    {
                        var exited = marketPlugins.FirstOrDefault(p => p.Name == Path.GetFileNameWithoutExtension(plugin), new MarketPlugin() { Name = string.Empty});
                        if (string.IsNullOrEmpty(exited.Name))
                            continue;

                        marketPlugins.Remove(exited);
                    }

                    var responseData = new
                    {
                        success = true,
                        plugins = marketPlugins
                    };

                    await WriteJsonResponseAsync(response, responseData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in Marketplace: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to get products." });
            }
        }

        private async Task HandleMarketDownloadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST method is allowed." });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var downloadRequest = JsonConvert.DeserializeObject<MarketPlugin>(body);

                if (downloadRequest == null || string.IsNullOrWhiteSpace(downloadRequest.DownloadLink) || string.IsNullOrWhiteSpace(downloadRequest.Name))
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Request body must contain a valid 'DownloadLink' and 'Name' property." });
                    return;
                }
                _downloadJobProcessor.EnqueueJob(downloadRequest);

                response.StatusCode = 202;
                await WriteJsonResponseAsync(response, new
                {
                    success = true,
                    message = $"Download request for '{downloadRequest.Name}.dll' has been queued and will be processed."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in Marketplace download handler: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "An internal error occurred while queuing the download request." });
            }
        }

        private async Task HandleProcessListAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            Process[] processes = Process.GetProcesses();
            var processesData = new
            {
                processesName = processes.Select(p => p.ProcessName).ToList()
            };

            await WriteJsonResponseAsync(response, processesData);
        }

        private async Task HandleProcessKill(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST method is allowed." });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var processStopRequest = JsonConvert.DeserializeObject<StopProcessRequest>(body);

                if (processStopRequest == null || string.IsNullOrWhiteSpace(processStopRequest.ProcessName))
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Request body must contain a valid 'ProcessName' property." });
                    return;
                }

                var processes = Process.GetProcessesByName(processStopRequest.ProcessName);

                if (!processes.Any())
                {
                    response.StatusCode = 404;
                    await WriteJsonResponseAsync(response, new { success = false, message = $"Process '{processStopRequest.ProcessName}' not found." });
                    return;
                }

                foreach (var process in processes)
                {
                    process.Kill();
                }

                response.StatusCode = 200;
                await WriteJsonResponseAsync(response, new
                {
                    success = true,
                    message = $"Process '{processStopRequest.ProcessName}' has been terminated."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in stopping process: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "An internal error occurred while stopping the process." });
            }
        }

        private async Task HandlePluginToggleAsync(HttpListenerRequest request, HttpListenerResponse response, string pluginId, bool enable)
        {
            await WriteJsonResponseAsync(response, new
            {
                success = true,
                message = $"Plugin {pluginId} {(enable ? "enabled" : "disabled")} successfully"
            });
        }


        private bool ValidateAdminAuthentication(HttpListenerRequest request)
        {
            string authHeader = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                return false;
            }

            string token = authHeader.Substring("Bearer ".Length);
            if (_authService.ValidateJwtToken(token))
                if (_authService.GetRoleFromToken(token).ToLower() == "admin")
                    return true;

            return false;
        }

        private bool ValidateUserAuthentication(HttpListenerRequest request)
        {
            string authHeader = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                return false;
            }

            string token = authHeader.Substring("Bearer ".Length);
            return _authService.ValidateJwtToken(token);
        }

        private void SendUnauthorized(HttpListenerResponse response)
        {
            response.StatusCode = 401;
            response.AddHeader("WWW-Authenticate", "Bearer");
        }


        private async Task HandleStatsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var stats = new
            {
                uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
                users = _userService.usersCount,
                maxConnections = _configManager.Config.MaxConnections,
                protocol = 1
            };
            await WriteJsonResponseAsync(response, stats);
        }

        private async Task HandleSystemAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var proc = Process.GetCurrentProcess();
            double cpuUse = _lastCpuUsage;
            double memUsage = proc.WorkingSet64 / 1024.0 / 1024.0;
            object systemStats;

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    string memInfo = File.ReadAllText("/proc/meminfo");
                    string totalLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal:"));
                    string freeLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemAvailable:"));
                    double totalMem = totalLine != null ? double.Parse(new string(totalLine.Where(char.IsDigit).ToArray())) / 1024.0 : memUsage;
                    double freeMem = freeLine != null ? double.Parse(new string(freeLine.Where(char.IsDigit).ToArray())) / 1024.0 : 0;
                    memUsage = totalMem - freeMem;

                    DriveInfo drive = new DriveInfo("/");
                    long totalSpace = drive.TotalSize;
                    long usedSpace = totalSpace - drive.AvailableFreeSpace;

                    long totalBytesSent = 0, totalBytesReceived = 0;
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        var nicStats = nic.GetIPv4Statistics();
                        totalBytesSent += nicStats.BytesSent;
                        totalBytesReceived += nicStats.BytesReceived;
                    }

                    activePlugins = _pluginManager.GetLoadedPlugins().Count;

                    systemStats = new
                    {
                        cpuUsage = cpuUse,
                        memoryMB = memUsage,
                        diskUsedGB = usedSpace / (1024.0 * 1024 * 1024),
                        diskTotalGB = totalSpace / (1024.0 * 1024 * 1024),
                        netSentMB = totalBytesSent / (1024.0 * 1024),
                        netReceivedMB = totalBytesReceived / (1024.0 * 1024),
                        activeConnections = activeConnections,
                        activePlugins = activePlugins
                    };
                }
                catch { systemStats = new { cpuUsage = cpuUse, memoryMB = memUsage }; }
            }
            else
            {
                systemStats = new { cpuUsage = cpuUse, memoryMB = memUsage };
            }

            await WriteJsonResponseAsync(response, systemStats);
        }

        private async Task HandleLogsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "latest.log");
            string[] lines = File.Exists(logFile) ? File.ReadLines(logFile).Reverse().Take(200).Reverse().ToArray() : Array.Empty<string>();
            await WriteJsonResponseAsync(response, lines);
        }

        private async Task HandleVideosAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                if (request.HttpMethod == "GET")
                {
                    string[] files = _videoService.GetVideoFiles();
                    await WriteJsonResponseAsync(response, files);
                }
                else
                {
                    response.StatusCode = 405;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Method not allowed" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in HandleVideos: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Internal server error" });
            }
        }

        private async Task HandleVideoUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                if (json != null && json.TryGetValue("url", out var videoUrl))
                {
                    var (success, message) = await _videoService.DownloadVideoFromUrl(videoUrl);
                    if (success)
                        await WriteJsonResponseAsync(response, new { success = true, message });
                    else
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponseAsync(response, new { success = false, message });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid JSON body - url required" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"URL Download failed: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Download failed: " + ex.Message });
            }
        }


        private async Task ServeVideoAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                string path = request.Url.AbsolutePath;
                if (!path.StartsWith("/videos/")) { response.StatusCode = 400; await WriteStringResponseAsync(response, "Invalid URL format"); return; }

                string fileName = path.Substring("/videos/".Length);
                int queryIndex = fileName.IndexOf('?');
                if (queryIndex > 0) { fileName = fileName.Substring(0, queryIndex); }

                string filePath = _videoService.GetVideoFilePath(fileName);
                if (filePath == null) { response.StatusCode = 404; await WriteStringResponseAsync(response, "Video file not found"); return; }

                string contentType = _videoService.GetContentType(filePath);
                response.ContentType = contentType;
                response.AddHeader("Accept-Ranges", "bytes");
                long fileLength = new FileInfo(filePath).Length;

                if (request.Headers["Range"] != null)
                {
                    string range = request.Headers["Range"].Replace("bytes=", "");
                    string[] parts = range.Split('-');
                    long start = parts.Length > 0 && long.TryParse(parts[0], out var s) ? s : 0;
                    long end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? e : fileLength - 1;

                    if (start >= fileLength || end >= fileLength || start > end) { response.StatusCode = 416; response.AddHeader("Content-Range", $"bytes */{fileLength}"); return; }

                    long contentLength = end - start + 1;
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                    response.ContentLength64 = contentLength;

                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.Seek(start, SeekOrigin.Begin);
                        byte[] buffer = new byte[8192];
                        int bytesRead;
                        long bytesRemaining = contentLength;
                        while (bytesRemaining > 0 && (bytesRead = await fs.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, bytesRemaining))) > 0)
                        {
                            await response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                            bytesRemaining -= bytesRead;
                        }
                    }
                }
                else
                {
                    response.ContentLength64 = fileLength;
                    using (var fs = File.OpenRead(filePath)) { await fs.CopyToAsync(response.OutputStream); }
                }
            }
            catch (Exception ex) { _logger.LogError($"Error serving video: {ex.Message}"); response.StatusCode = 500; }
        }

        private async Task ServeDefaultPageAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!string.IsNullOrWhiteSpace(panelWebPage))
            {
                byte[] buffer = Encoding.UTF8.GetBytes(panelWebPage);
                response.ContentType = "text/html";
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else
            {
                response.StatusCode = 404;
            }
        }


        private async Task WriteJsonResponseAsync(HttpListenerResponse response, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private async Task WriteStringResponseAsync(HttpListenerResponse response, string text)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            response.ContentType = "text/plain";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// Holds the HttpListenerResponse for the current asynchronous request context.
    /// </summary>
    public class HttpContextHolder
    {
        private static readonly AsyncLocal<HttpListenerResponse> _currentResponse = new();

        public static HttpListenerResponse CurrentResponse
        {
            get => _currentResponse.Value;
            set => _currentResponse.Value = value;
        }
    }

    public class MarketPlugin
    {
        public string Name { get; set; } = "plugin";
        public string Description { get; set; } = "description";
        public string DownloadLink { get; set; } = "";
        public bool isPackaged = false;
    }

    public class DownloadJobProcessor
    {
        private readonly ConcurrentQueue<MarketPlugin> _jobQueue = new ConcurrentQueue<MarketPlugin>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly Logger _logger;
        private readonly PluginManager _pluginManager;
        private readonly PackageManager _packageManager;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Task _processingTask;

        public DownloadJobProcessor(Logger logger, PluginManager pluginManager, PackageManager packageManager)
        {
            _logger = logger;
            _pluginManager = pluginManager;
            _packageManager = packageManager;
            _processingTask = Task.Run(ProcessJobsAsync);
        }

        public void EnqueueJob(MarketPlugin downloadRequest)
        {
            if (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _jobQueue.Enqueue(downloadRequest);
                _signal.Release();
            }
        }

        private async Task ProcessJobsAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_cancellationTokenSource.Token);

                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (_jobQueue.TryDequeue(out var downloadRequest))
                    {
                        try
                        {
                            await ProcessDownloadJobAsync(downloadRequest);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error processing market download job: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ProcessDownloadJobAsync(MarketPlugin downloadRequest)
        {
            if(downloadRequest.isPackaged == false)
            {
                string pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
                string fileName = $"{downloadRequest.Name}.dll";
                string filePath = Path.Combine(pluginsDirectory, fileName);

                Directory.CreateDirectory(pluginsDirectory);

                using var client = new HttpClient();
                var downloadResponse = await client.GetAsync(downloadRequest.DownloadLink, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource.Token);

                if (!downloadResponse.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to download file from URL: {downloadRequest.DownloadLink}. Status: {downloadResponse.StatusCode}");
                    return;
                }

                using (var contentStream = await downloadResponse.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream, _cancellationTokenSource.Token);
                }

                _logger.Log($"Successfully downloaded and saved plugin to: {filePath}");
                await _pluginManager.ReloadAllPluginsAsync();
            }
            else
            {
                string pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
                Directory.CreateDirectory(pluginsDirectory);

                using var client = new HttpClient();
                var downloadResponse = await client.GetAsync(downloadRequest.DownloadLink, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource.Token);

                if (!downloadResponse.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to download file from URL: {downloadRequest.DownloadLink}. Status: {downloadResponse.StatusCode}");
                    return;
                }

                var content = await downloadResponse.Content.ReadAsByteArrayAsync();
                var package = await _packageManager.GetPackageFromByteArray(content);

                if (package != null)
                    await _packageManager.InstallPackage(package);
                else
                    _logger.LogError($"Can't Install Packaged Plugin: '{downloadRequest.Name}' From '{downloadRequest.DownloadLink}'");
            }
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            await _processingTask;
        }
    }
}