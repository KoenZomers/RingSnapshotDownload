using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace KoenZomers.Ring.SnapshotDownload
{
    /// <summary>
    /// Configuration to use for downloading the Ring Snapshot
    /// </summary>
    public class Configuration
    {
        /// <summary>
        /// Path where to download the snapshot to
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Username to use to connect to Ring
        /// </summary>
        [JsonPropertyName("RingUsername")]
        public string Username { get; set; }

        /// <summary>
        /// Password to use to connect to Ring
        /// </summary>
        [JsonPropertyName("RingPassword")]
        public string Password { get; set; }

        /// <summary>
        /// RefreshToken to use to connect to Ring
        /// </summary>
        [JsonPropertyName("RingRefreshToken")]
        public string RefreshToken { get; set; }

        /// <summary>
        /// Type ID of the Ring device to download the snapshot from
        /// </summary>
        [JsonPropertyName("RingDeviceId")]
        public int? DeviceId { get; set; }

        /// <summary>
        /// Boolean indicating if a listing of available bots should be returned
        /// </summary>
        [JsonIgnore]
        public bool ListBots { get; set; } = false;

        /// <summary>
        /// Path to where the configuration is stored
        /// </summary>
        [JsonIgnore]
        public string ConfigFilePath { get; set; }

        /// <summary>
        /// Boolean indicating if a fresh snapshot should be requested from the Ring device before downloading it. If set to false, the latest cached snapshot will be used which is faster.
        /// </summary>
        public bool ForceUpdateSnapshot { get; set; } = true;

        /// <summary>
        /// Amount of times to retry downloading a snapshot if a 404 not found is being returned
        /// </summary>
        public short MaximumRetries { get; set; } = 3;

        /// <summary>
        /// Tries to retrieve the configuration from the configuration file located at <paramref name="path"/>
        /// </summary>
        /// <param name="path">Full path to the file containing the application settings</param>
        /// <returns>Configuration instance</returns>
        public static async Task<Configuration> Load(string path)
        {
            var configuration = new Configuration();

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var settingsFileContents = await File.ReadAllTextAsync(path);
                configuration = JsonSerializer.Deserialize<Configuration>(settingsFileContents);
            }
            
            configuration.ConfigFilePath = path;

            return configuration;
        }

        /// <summary>
        /// Stores the current configuration in the application configuration file
        /// </summary>
        public async void Save()
        {
            await File.WriteAllTextAsync(ConfigFilePath, JsonSerializer.Serialize(this));
        }
    }
}
