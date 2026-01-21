using Newtonsoft.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UltimateServer.Models;

namespace UltimateServer.Services
{
    public class CommandHandler
    {
        private readonly AuthenticationService _authenticationService;
        private readonly UserService _userService;
        private readonly Logger _logger;
        private readonly Dictionary<string, Func<Data, Data>> _commandHandlers;

        public CommandHandler(UserService userService, Logger logger, AuthenticationService authenticationService)
        {
            _authenticationService = authenticationService;
            _userService = userService;
            _logger = logger;
            _commandHandlers = new Dictionary<string, Func<Data, Data>>(StringComparer.OrdinalIgnoreCase);
            RegisterCommands();
        }

        private void RegisterCommands()
        {
            AddCommand("createUser", CreateUserCommand);
            AddCommand("say", SayCommand);
            AddCommand("makeUUID", MakeUUIDCommand);
        }

        private Data CreateUserCommand(Data req)
        {
            try
            {
                var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(req.jsonData);
                if (registerRequest == null)
                {
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = "Invalid request data." };
                }

                var (user, message) = _userService.CreateUserAsync(registerRequest).GetAwaiter().GetResult();

                if (user != null)
                {
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = $"User {user.Username} created." };
                }
                else
                {
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = $"Error: {message}" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in CreateUserCommand: {ex.Message}");
                return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = "An unexpected error occurred." };
            }
        }

        private Data SayCommand(Data req)
        {
            _logger.Log($"🗨️ Client says: {req.jsonData}");
            return new Data { protocolVersion = 1, theCommand = "reply", jsonData = $"Server received: {req.jsonData}" };
        }

        private Data MakeUUIDCommand(Data req)
        {
            var uuid = Guid.NewGuid();
            _logger.Log($"🆔 Generated UUID: {uuid}");
            return new Data { protocolVersion = 1, theCommand = "uuid", jsonData = JsonConvert.SerializeObject(uuid) };
        }

        private void AddCommand(string name, Func<Data, Data> handler) => _commandHandlers[name] = handler;

        public bool TryHandleCommand(Data request, out Data response)
        {
            /*if (!ValidateRequester(request.userName, request.encryptedPassword))
            {
                response = new() { theCommand = "Invalid User" };
                return false;
            }*/

            response = null;
            if (_commandHandlers.TryGetValue(request.theCommand, out var handler))
            {
                response = handler(request);
                return true;
            }
            return false;
        }

        /*/// <summary>
        /// Validates if the requester is a valid user and its password is correct.
        /// </summary>
        public bool ValidateRequester(string username, string encryptedPassword)
        {
            var user = _userService.Users.FirstOrDefault(user => user.Username == username, null);
            if (user == null)
                return false;

            string decryptedPassword = GetTheDecryptedPassword(encryptedPassword, user.uuid.ToString());
            if (user.Password.Equals(decryptedPassword))
                return true;
            else
                return false;
        }*/

        /*/// <summary>
        /// Decrypts an encrypted password using the uuid of that user
        /// </summary>
        public string GetTheDecryptedPassword(string encryptedPassword, string uuid)
        {
            try
            {
                if (encryptedPassword != null)
                    if (uuid != null)
                        return SecureEncryption.Decrypt(encryptedPassword, uuid);
                    else
                        return null;
                else
                    return null;
            }
            catch
            {
                return null;
            }
        }*/
    }
}

public static class SecureEncryption
{
    private const int KeySize = 256;

    private const int DerivationIterations = 10000;

    private const int SaltSize = 32;
    private const int IvSize = 16;

    public static string Encrypt(string plainText, string passPhrase)
    {
        var saltBytes = GenerateRandomBytes(SaltSize);
        var ivBytes = GenerateRandomBytes(IvSize);
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);

        using (var password = new Rfc2898DeriveBytes(passPhrase, saltBytes, DerivationIterations, HashAlgorithmName.SHA256))
        {
            var keyBytes = password.GetBytes(KeySize / 8);
            using (var aes = Aes.Create())
            {
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = keyBytes;
                aes.IV = ivBytes;

                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        memoryStream.Write(saltBytes, 0, saltBytes.Length);
                        memoryStream.Write(ivBytes, 0, ivBytes.Length);

                        using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
                        {
                            cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                            cryptoStream.FlushFinalBlock();

                            return Convert.ToBase64String(memoryStream.ToArray());
                        }
                    }
                }
            }
        }
    }

    public static string Decrypt(string cipherText, string passPhrase)
    {
        try
        {
            var cipherTextBytesWithSaltAndIv = Convert.FromBase64String(cipherText);

            var saltBytes = cipherTextBytesWithSaltAndIv.Take(SaltSize).ToArray();

            var ivBytes = cipherTextBytesWithSaltAndIv.Skip(SaltSize).Take(IvSize).ToArray();

            var cipherTextBytes = cipherTextBytesWithSaltAndIv
                .Skip(SaltSize + IvSize)
                .Take(cipherTextBytesWithSaltAndIv.Length - SaltSize - IvSize)
                .ToArray();

            using (var password = new Rfc2898DeriveBytes(passPhrase, saltBytes, DerivationIterations, HashAlgorithmName.SHA256))
            {
                var keyBytes = password.GetBytes(KeySize / 8);
                using (var aes = Aes.Create())
                {
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = keyBytes;
                    aes.IV = ivBytes;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    {
                        using (var memoryStream = new MemoryStream(cipherTextBytes))
                        {
                            using (var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read))
                            using (var streamReader = new StreamReader(cryptoStream, Encoding.UTF8))
                            {
                                return streamReader.ReadToEnd();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Failed to decrypt the text.", ex);
        }
    }

    private static byte[] GenerateRandomBytes(int count)
    {
        var randomBytes = new byte[count];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }
        return randomBytes;
    }

    public static string GeneratePasswordHash(string password, string salt = null)
    {
        byte[] saltBytes;
        if (salt == null)
        {
            saltBytes = GenerateRandomBytes(SaltSize);
            salt = Convert.ToBase64String(saltBytes);
        }
        else
        {
            saltBytes = Convert.FromBase64String(salt);
        }

        using (var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, DerivationIterations, HashAlgorithmName.SHA256))
        {
            byte[] hash = pbkdf2.GetBytes(32);
            return Convert.ToBase64String(hash);
        }
    }

    public static bool VerifyPassword(string password, string hash, string salt)
    {
        try
        {
            string computedHash = GeneratePasswordHash(password, salt);
            return hash == computedHash;
        }
        catch
        {
            return false;
        }
    }
}
