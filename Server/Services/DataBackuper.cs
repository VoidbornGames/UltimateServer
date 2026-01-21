using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Services
{
    class DataBackuper
    {
        public List<string> _serverFiles;
        public List<string> _siteDirectories;

        private string _backupPath = "Backups";
        private int _backupPerHour;
        private bool _backupSites;
        private Logger _logger;
        private SitePress _sitePress;

        public DataBackuper(Logger logger, FilePaths filePaths, SitePress sitePress, DataBox dataBox, ConfigManager configManager)
        {
            _logger = logger;
            _sitePress = sitePress;
            _serverFiles = new List<string>();
            _siteDirectories = new List<string>();

            _backupPerHour = configManager.Config.BackupPerHour;
            _backupPath = configManager.Config.BackupFolder;
            _backupSites = configManager.Config.BackupSites;

            _serverFiles.Add(filePaths.ConfigFile);
            _serverFiles.Add(dataBox._options.SaveFile);
            _serverFiles.Add(configManager.Config.MiniDB_Options.IndexFile);
            _serverFiles.Add(configManager.Config.MiniDB_Options.DatabaseFile);
            _serverFiles.Add("sftp.json");
            _serverFiles.Add("data.db");

            foreach (var file in Directory.GetFiles("logs"))
            {
                _serverFiles.Add(file);
            }
        }

        public async Task Start()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await BackupServer();
                    await Task.Delay(TimeSpan.FromHours(_backupPerHour));
                }
            });
        }

        private async Task BackupServer()
        {
            _siteDirectories.Clear();

            if (_backupSites)
            {
                foreach (var site in _sitePress.sites)
                {
                    string sitePath = Path.Combine("/var/www/", site.Key);
                    if (Directory.Exists(sitePath))
                    {
                        _siteDirectories.Add(sitePath);
                    }
                    else
                    {
                        _logger.LogWarning($"[DataBackuper] Site directory not found: {sitePath}");
                    }
                }
            }

            try
            {
                if (!Directory.Exists(_backupPath)) Directory.CreateDirectory(_backupPath);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var destinationZipFilePath = Path.Combine(_backupPath, $"Backup_{timestamp}.zip");

                using (FileStream zipToCreate = new FileStream(destinationZipFilePath, FileMode.Create))
                using (ZipArchive archive = new ZipArchive(zipToCreate, ZipArchiveMode.Create))
                {
                    foreach (string fileToAdd in _serverFiles)
                    {
                        if (File.Exists(fileToAdd))
                        {
                            string entryName = Path.GetFileName(fileToAdd);

                            ZipArchiveEntry zipEntry = archive.CreateEntry(entryName);

                            using (FileStream fileStream = new FileStream(fileToAdd, FileMode.Open, FileAccess.Read))
                            using (Stream entryStream = zipEntry.Open())
                            {
                                fileStream.CopyTo(entryStream);
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"[DataBackuper] File not found and skipped: {fileToAdd}");
                        }
                    }

                    foreach (string directoryToAdd in _siteDirectories)
                    {
                        AddDirectoryToZip(archive, directoryToAdd, Path.GetFileName(directoryToAdd));
                    }
                }

                _logger.Log($"📼 Backup complete! Archive created at: {destinationZipFilePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[DataBackuper] An error occurred: {ex.Message}");
            }
        }

        private void AddDirectoryToZip(ZipArchive archive, string sourceDirectoryName, string entryName)
        {
            try
            {
                ZipArchiveEntry directoryEntry = archive.CreateEntry(entryName + "/");

                foreach (string file in Directory.GetFiles(sourceDirectoryName))
                {
                    string relativePath = Path.Combine(entryName, Path.GetFileName(file));
                    ZipArchiveEntry fileEntry = archive.CreateEntry(relativePath);

                    using (FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read))
                    using (Stream entryStream = fileEntry.Open())
                    {
                        fileStream.CopyTo(entryStream);
                    }
                }

                foreach (string directory in Directory.GetDirectories(sourceDirectoryName))
                {
                    string dirName = Path.GetFileName(directory);
                    string relativePath = Path.Combine(entryName, dirName);
                    AddDirectoryToZip(archive, directory, relativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[DataBackuper] Error adding directory {sourceDirectoryName} to zip: {ex.Message}");
            }
        }
    }
}