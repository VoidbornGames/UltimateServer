using Newtonsoft.Json;
using System.Text;

namespace UltimateServer.Services
{
    /// <summary>
    /// Manages website creation, configuration, and deletion on a server.
    /// Handles Nginx configuration, PHP-FPM pool management, SSL certificates, and SFTP user accounts.
    /// </summary>
    class SitePress
    {
        private Logger _logger;
        private Nginx _nginx;
        private SftpServer _sftpServer;
        private UserService _userService;
        private AuthenticationService _authenticationService;

        private string templateFolder;

        /// <summary>
        /// Gets the path to the sites configuration file
        /// </summary>
        public string sitesConfig { get; private set; } = "sites.json";

        /// <summary>
        /// Dictionary containing site names and their corresponding ports
        /// </summary>
        public Dictionary<string, int> sites;


        public SitePress(Logger logger, Nginx nginx, SftpServer sftpServer, UserService userService, AuthenticationService authenticationService)
        {
            _logger = logger;
            _nginx = nginx;
            _sftpServer = sftpServer;
            templateFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Template");
            _userService = userService;
            _authenticationService = authenticationService;
        }

        /// <summary>
        /// Starts the SitePress service by loading existing sites
        /// </summary>
        public async Task Start()
        {
            sites = await LoadSites();
            _logger.Log("🎨 SitePress has been started!");
        }

        /// <summary>
        /// Stops the SitePress service and saves the current sites configuration
        /// </summary>
        public async Task StopAsync()
        {
            await SaveSites();
            _logger.Log("🎨 SitePress stopped");
        }

        /// <summary>
        /// Creates a new website with the specified name and port
        /// </summary>
        public async Task<bool> CreateSite(string name, int port)
        {
            var sitePath = "/var/www/" + name;

            try
            {
                Directory.CreateDirectory(sitePath);
                CopyDirectory(templateFolder, sitePath);

                await CreatePhpFpmPool(name, sitePath);

                var httpConfig = _nginx.configHttp
                    .Replace("%SiteName%", name)
                    .Replace("%SitePath%", sitePath)
                    .Replace("%SitePort%", port.ToString());

                var siteConf = $"/etc/nginx/sites-available/{name}.conf";
                File.WriteAllText(siteConf, httpConfig);

                await _nginx.RunCommand("sudo", $"ln -sf {siteConf} /etc/nginx/sites-enabled/");
                await _nginx.RunCommand("sudo", "nginx -t");
                await _nginx.RunCommand("sudo", "systemctl reload nginx");

                await _nginx.RunCommand("sudo", $"certbot --nginx -d {name} --non-interactive --agree-tos -m admin@{name} --redirect");

                var sslConfig = _nginx.configSSL
                    .Replace("%SiteName%", name)
                    .Replace("%SitePath%", sitePath);

                File.WriteAllText(siteConf, sslConfig);
                await _nginx.RunCommand("sudo", "nginx -t");
                await _nginx.RunCommand("sudo", "systemctl reload nginx");

                sites.Add(name, port);
                await SaveSites();

                var pass = SimplePasswordGenerator.Generate();
                await _sftpServer.CreateUser(name, pass);

                _logger.Log($"👀 SitePress: SFTP user {name} has been created with password: {pass}");
                _logger.Log($"✅ Site {name} created successfully with PHP-FPM isolation");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to create site {name}: {e.Message}");
                await DeletePhpFpmPool(name);
                return false;
            }
        }

        /// <summary>
        /// Deletes an existing website
        /// </summary>
        public async Task<bool> DeleteSite(string name)
        {
            var sitePath = "/var/www/" + name;

            if (!Directory.Exists(sitePath))
                return false;

            try
            {
                string siteConf = "/etc/nginx/sites-available/" + name + ".conf";

                File.Delete(siteConf);
                File.Delete("/etc/nginx/sites-enabled/" + name + ".conf");

                await DeletePhpFpmPool(name);

                await _nginx.RunCommand("sudo", $"rm -rf {sitePath}");
                await _nginx.RunCommand("sudo", "systemctl reload nginx");

                sites.Remove(name);
                await SaveSites();

                await _sftpServer.DeleteUser(name);

                _logger.Log($"✅ Site {name} deleted successfully");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to delete site {name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a PHP-FPM pool configuration for a specific site
        /// </summary>
        private async Task CreatePhpFpmPool(string siteName, string sitePath)
        {
            var poolConfig = _nginx.poolConfig
                .Replace("%SiteName%", siteName)
                .Replace("%SitePath%", sitePath);

            var poolFilePath = $"/etc/php/8.3/fpm/pool.d/{siteName}.conf";

            await File.WriteAllTextAsync(poolFilePath, poolConfig);

            await _nginx.RunCommand("sudo", "systemctl restart php8.3-fpm");
        }

        /// <summary>
        /// Deletes a PHP-FPM pool configuration for a specific site
        /// </summary>
        private async Task DeletePhpFpmPool(string siteName)
        {
            var poolFilePath = $"/etc/php/8.3/fpm/pool.d/{siteName}.conf";

            if (File.Exists(poolFilePath))
            {
                File.Delete(poolFilePath);
                await _nginx.RunCommand("sudo", "systemctl restart php8.3-fpm");
                _logger.Log($"✅ PHP-FPM pool deleted for {siteName}");
            }
        }

        /// <summary>
        /// Saves the sites configuration to a JSON file
        /// </summary>
        public async Task SaveSites()
        {
            var _sites = JsonConvert.SerializeObject(sites);
            await File.WriteAllTextAsync(sitesConfig, _sites);
        }

        /// <summary>
        /// Loads the sites configuration from a JSON file
        /// </summary>
        public async Task<Dictionary<string, int>> LoadSites()
        {
            if (File.Exists(sitesConfig))
            {
                var _sites = JsonConvert.DeserializeObject<Dictionary<string, int>>(await File.ReadAllTextAsync(sitesConfig));
                if (_sites == null) return new Dictionary<string, int>();
                else return _sites;
            }
            return new Dictionary<string, int>();
        }

        /// <summary>
        /// Recursively copies a directory and its contents to another location
        /// </summary>
        static void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(directory));
                CopyDirectory(directory, destSubDir);
            }
        }
    }

    /// <summary>
    /// Utility class for generating simple random passwords
    /// </summary>
    public static class SimplePasswordGenerator
    {
        private const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*";

        /// <summary>
        /// Generates a random password with the specified length
        /// </summary>
        public static string Generate(int length = 16)
        {
            var random = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                random.Append(Chars[Random.Shared.Next(Chars.Length)]);
            }
            return random.ToString();
        }
    }
}