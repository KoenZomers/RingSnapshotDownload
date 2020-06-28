using System;
using KoenZomers.Ring.Api;
using System.Configuration;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Net;

namespace KoenZomers.Ring.SnapshotDownload
{
    class Program
    {
        /// <summary>
        /// Refresh token to use to authenticate to the Ring API
        /// </summary>
        public static string RefreshToken
        {
            get { return ConfigurationManager.AppSettings["RefreshToken"]; }
            set
            {
                var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                if (configFile.AppSettings.Settings["RefreshToken"] == null)
                {
                    configFile.AppSettings.Settings.Add("RefreshToken", value);
                }
                else
                {
                    configFile.AppSettings.Settings["RefreshToken"].Value = value;
                }
                configFile.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection(configFile.AppSettings.SectionInformation.Name);
            }
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine();

            var appVersion = Assembly.GetExecutingAssembly().GetName().Version;

            Console.WriteLine($"Ring Snapshot Download Tool v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}.{appVersion.Revision} by Koen Zomers");
            Console.WriteLine();

            // Ensure arguments have been provided
            if (args.Length == 0)
            {
                DisplayHelp();
                Environment.Exit(1);
            }

            // Parse the provided arguments
            var configuration = ParseArguments(args);

            // Ensure we have the required configuration
            if (string.IsNullOrWhiteSpace(configuration.Username) && string.IsNullOrWhiteSpace(RefreshToken))
            {
                Console.WriteLine("-username is required");
                Environment.Exit(1);
            }

            if (string.IsNullOrWhiteSpace(configuration.Password) && string.IsNullOrWhiteSpace(RefreshToken))
            {
                Console.WriteLine("-password is required");
                Environment.Exit(1);
            }

            if (!configuration.DeviceId.HasValue && !configuration.ListBots)
            {
                Console.WriteLine("-deviceid or -list is required");
                Environment.Exit(1);
            }

            // Connect to Ring
            Console.WriteLine("Connecting to Ring services");
            Session session;
            if (!string.IsNullOrWhiteSpace(RefreshToken))
            {
                // Use refresh token from previous session
                Console.WriteLine("Authenticating using refresh token from previous session");

                session = await Session.GetSessionByRefreshToken(RefreshToken);
            }
            else
            {
                // Use the username and password provided
                Console.WriteLine("Authenticating using provided username and password");

                session = new Session(configuration.Username, configuration.Password);

                try
                {
                    await session.Authenticate();
                }
                catch (Api.Exceptions.TwoFactorAuthenticationRequiredException)
                {
                    // Two factor authentication is enabled on the account. The above Authenticate() will trigger a text or e-mail message to be sent. Ask for the token sent in that message here.
                    Console.WriteLine($"Two factor authentication enabled on this account, please enter the token received in the text message on your phone or the e-mail from Ring:");
                    var token = Console.ReadLine();

                    // Authenticate again using the two factor token
                    await session.Authenticate(twoFactorAuthCode: token);
                }
                catch(Api.Exceptions.ThrottledException)
                {
                    Console.WriteLine("Two factor authentication is required, but too many tokens have been requested recently. Wait for a few minutes and try connecting again.");
                    Environment.Exit(1);
                }
                catch (WebException)
                {
                    Console.WriteLine("Connection failed. Validate your credentials.");
                    Environment.Exit(1);
                }
            }

            // If we have a refresh token, update the config file with it so we don't need to authenticate again next time
            if (session.OAuthToken != null)
            {
                RefreshToken = session.OAuthToken.RefreshToken;
            }

            if (configuration.ListBots)
            {
                // Retrieve all available Ring devices and list them
                Console.Write("Retrieving all devices... ");
                
                var devices = await session.GetRingDevices();

                Console.WriteLine($"{devices.Doorbots.Count + devices.AuthorizedDoorbots.Count + devices.StickupCams.Count} found");
                Console.WriteLine();

                if (devices.AuthorizedDoorbots.Count > 0)
                {
                    Console.WriteLine("Authorized Doorbells");
                    foreach (var device in devices.AuthorizedDoorbots)
                    {
                        Console.WriteLine($"{device.Id} - {device.Description}");
                    }
                    Console.WriteLine();
                }
                if (devices.Doorbots.Count > 0)
                {
                    Console.WriteLine("Doorbells");
                    foreach (var device in devices.Doorbots)
                    {
                        Console.WriteLine($"{device.Id} - {device.Description}");
                    }
                    Console.WriteLine();
                }
                if (devices.StickupCams.Count > 0)
                {
                    Console.WriteLine("Stickup cams");
                    foreach (var device in devices.StickupCams)
                    {
                        Console.WriteLine($"{device.Id} - {device.Description}");
                    }
                    Console.WriteLine();
                }
            }
            else
            {
                if(configuration.ForceUpdateSnapshot)
                {
                    Console.WriteLine("Requesting Ring device to capture a new snapshot");
                    await session.UpdateSnapshot(configuration.DeviceId.Value);

                    // Give it timt to process the update
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }

                // By default the screenshot will be tagged with the current date/time unless we can retrieve information from Ring when the latest snapshot was really taken
                var timeStamp = DateTime.Now;

                // Retrieve when the latest available snapshot was taken
                var doorbotTimeStamps = await session.GetDoorbotSnapshotTimestamp(configuration.DeviceId.Value);
                
                // Validate if we received timestamps
                if(doorbotTimeStamps.Timestamp.Count > 0)
                {
                    // Filter out timestamps which are not for the doorbot we are requesting and take the most recent snapshot only
                    var latestDoorbotTimeStamp = doorbotTimeStamps.Timestamp.Where(t => t.DoorbotId == configuration.DeviceId.Value.ToString()).OrderByDescending(t => t.TimestampEpoch).FirstOrDefault();

                    // If we have a result and the result has an Epoch timestamp on it, use that as the marker for when the screenshot has been taken
                    if (latestDoorbotTimeStamp != null && latestDoorbotTimeStamp.TimestampEpoch.HasValue)
                    {
                        // Convert from the Epoch time to a DateTime we can use
                        timeStamp = latestDoorbotTimeStamp.Timestamp.Value;
                    }
                }

                // Construct the filename and path where to save the file
                var downloadFileName = $"{configuration.DeviceId} - {timeStamp:yyyy-MM-dd HH-mm-ss}.jpg";
                var downloadFullPath = Path.Combine(configuration.OutputPath, downloadFileName);

                // Retrieve the snapshot
                Console.WriteLine($"Downloading snapshot from Ring device with ID {configuration.DeviceId} to {downloadFullPath}");
                short attempt = 0;
                do
                {
                    attempt++;
                    try
                    {
                        await session.GetLatestSnapshot(configuration.DeviceId.Value, downloadFullPath);
                        break;
                    }
                    catch (WebException e) when (e.Message.Contains("404"))
                    {
                        // Ring tends to throw a 404 if it has no snapshot available and couldn't retrieve one in time, retry it
                        Console.WriteLine($"Not found returned by Ring API, retrying ({attempt}/{configuration.MaximumRetries})");
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }
                } while (attempt < configuration.MaximumRetries);

                Console.WriteLine();
            }
            
            Console.WriteLine("Done");

            Environment.Exit(0);
        }

        /// <summary>
        /// Parses all provided arguments
        /// </summary>
        /// <param name="args">String array with arguments passed to this console application</param>
        private static Configuration ParseArguments(IList<string> args)
        {
            var configuration = new Configuration
            {
                Username = ConfigurationManager.AppSettings["RingUsername"],
                Password = ConfigurationManager.AppSettings["RingPassword"],
                OutputPath = Environment.CurrentDirectory
            };

            if (args.Contains("-out"))
            {
                configuration.OutputPath = args[args.IndexOf("-out") + 1];
            }

            if (args.Contains("-username"))
            {
                configuration.Username = args[args.IndexOf("-username") + 1];
            }

            if (args.Contains("-password"))
            {
                configuration.Password = args[args.IndexOf("-password") + 1];
            }

            if (args.Contains("-list"))
            {
                configuration.ListBots = true;
            }

            if (args.Contains("-forceupdate"))
            {
                configuration.ForceUpdateSnapshot = true;
            }

            if (args.Contains("-deviceid"))
            {
                if (int.TryParse(args[args.IndexOf("-deviceid") + 1], out int deviceId))
                {
                    configuration.DeviceId = deviceId;
                }
            }

            if (args.Contains("-maxretries"))
            {
                if (short.TryParse(args[args.IndexOf("-maxretries") + 1], out short maxretries))
                {
                    configuration.MaximumRetries = maxretries;
                }
            }

            return configuration;
        }

        /// <summary>
        /// Shows the syntax
        /// </summary>
        private static void DisplayHelp()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("   RingSnapshotDownload -username <username> -password <password> [-out <folder location> -deviceid <ring device id> -list -forceupdate]");
            Console.WriteLine();
            Console.WriteLine("username: Username of the account to use to log on to Ring");
            Console.WriteLine("password: Password of the account to use to log on to Ring");
            Console.WriteLine("out: The folder where to store the snapshot (optional, will use current directory if not specified)");
            Console.WriteLine("list: Returns the list with all Ring devices and their ids you can user with -deviceid");
            Console.WriteLine("deviceid: Id of the Ring device from wich you want to capture the screenshot. Use -list to retrieve all ids.");
            Console.WriteLine("forceupdate: Requests the Ring device to capture a new snapshot before downloading. If not provided, the latest cached snapshot will be taken.");
            Console.WriteLine("maxretries: Amount of times to retry downloading the snapshot when Ring returns an error. 3 is default.");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("   RingSnapshotDownload -username my@email.com -password mypassword -deviceid 12345 -forceupdate -out d:\\screenshots");
            Console.WriteLine("   RingSnapshotDownload -username my@email.com -password mypassword -deviceid 12345 -out d:\\screenshots");
            Console.WriteLine("   RingSnapshotDownload -username my@email.com -password mypassword -deviceid 12345");
            Console.WriteLine("   RingSnapshotDownload -username my@email.com -password mypassword -list");
            Console.WriteLine();
        }
    }
}
