using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.AntiRogue
{
    public static class AntiRogueTester
    {
        public const int ParallelTestingsCount = 100;
        public const int ExpectedRogueCount = 100;
        public const int ExpectedNonRogueCount = 5000;
        public const int ExpectedCannotDetermineCount = 5000;
        
        private const string Splitter = ",";
        private const string DateTimeFormatter = "yyyy-MM-dd HH:mm:ss.fffffff";
        private const int HttpsPort = 443;
        private const int TcpConnectTimeoutInMilli = 5000;
        private const int TestRounds = 5;
        
        
        private static readonly TimeSpan ExpireRogueAfter;
        private static readonly TimeSpan ExpireNonRogueAfter;
        private static readonly TimeSpan ExpireCannotDetermineAfter;
        private static readonly TimeSpan ExpireTestingStatusAfter;
        private static readonly TimeSpan FileSavingInterval;
        private static readonly TimeSpan CheckExpirationInterval;
        
        private static readonly ConcurrentDictionary<string, Expiration> Rogues;
        private static readonly ConcurrentDictionary<string, Expiration> NonRogues;
        private static readonly ConcurrentDictionary<string, Expiration> CannotDetermine;
        private static readonly ConcurrentDictionary<string, Expiration> Testings;
        private static readonly ConcurrentDictionary<ConcurrentDictionary<string, Expiration>, UpdateStatus> UpdateAndSaveTime;

        private static readonly List<WildcardDomainRecord> WhiteList;
        private static readonly List<WildcardDomainRecord> BlackList;
        private static readonly List<WildcardDomainRecord> BlockList;

        private static readonly Timer FileSavingTimer;
        private static readonly Timer CheckExpiringTimer;

        private static NameServerAddress[] _testingForwarders;
        private static DnsResolverConfig? _dnsResolverConfig;
        private static readonly object ConfigUpdateLock = new object();

        static AntiRogueTester()
        {
            Console.WriteLine("release-4 built on 2020-03-06");
            Console.WriteLine("====================  ANTI ROGUE INITIALIZATION  ====================");
            ExpireRogueAfter = TimeSpan.FromDays(7);
            ExpireNonRogueAfter = TimeSpan.FromDays(1);
            ExpireCannotDetermineAfter = TimeSpan.FromDays(1);
            ExpireTestingStatusAfter = TimeSpan.FromMinutes(5);
            FileSavingInterval = TimeSpan.FromMinutes(15);
            CheckExpirationInterval = TimeSpan.FromMinutes(1);

            Rogues = new ConcurrentDictionary<string, Expiration>(ParallelTestingsCount, ExpectedRogueCount);
            NonRogues = new ConcurrentDictionary<string, Expiration>(ParallelTestingsCount, ExpectedNonRogueCount);
            CannotDetermine = new ConcurrentDictionary<string, Expiration>(ParallelTestingsCount, ExpectedCannotDetermineCount);
            Testings = new ConcurrentDictionary<string, Expiration>(ParallelTestingsCount, ParallelTestingsCount);
            UpdateAndSaveTime = new ConcurrentDictionary<ConcurrentDictionary<string, Expiration>, UpdateStatus>(ParallelTestingsCount, 4);
            UpdateAndSaveTime[Rogues] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now};
            UpdateAndSaveTime[NonRogues] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now};
            UpdateAndSaveTime[CannotDetermine] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now};
            UpdateAndSaveTime[Testings] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now};
        
            WhiteList = new List<WildcardDomainRecord>();
            BlackList = new List<WildcardDomainRecord>();
            BlockList = new List<WildcardDomainRecord>();
            LoadFromFiles();
            
            FileSavingTimer = new Timer((state) =>
            {
                SaveToFiles();
            }, null, FileSavingInterval, FileSavingInterval);
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"File saving timer started with hash code [{FileSavingTimer.GetHashCode().ToString()}] " +
                $"and fires every [{FileSavingInterval.ToString()}]");
            CheckExpiringTimer = new Timer((state) =>
            {
                RenewAllExpiringRecords();
            }, null, CheckExpirationInterval, CheckExpirationInterval);
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Renew expiring record timer started with hash code [{CheckExpiringTimer.GetHashCode().ToString()}] " +
                $"and fires every [{CheckExpirationInterval.ToString()}]");
            Console.WriteLine("====================  ANTI ROGUE INITIALIZATION  ====================");
            Console.WriteLine();
        }

        private static void RenewAllExpiringRecords()
        {
            if (_testingForwarders == null || !_dnsResolverConfig.HasValue)
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Unable to renew all expiring records as [TestingForwarders] or " +
                    $"[DnsResolverConfig] is not passed to this static class yet.");
                return;
            }
            RenewIfExpiring(Rogues, "rogue");
            RenewIfExpiring(NonRogues, "non-rogue");
            RenewIfExpiring(CannotDetermine, "cannot-determine");
        }

        private static void RenewIfExpiring(ConcurrentDictionary<string, Expiration> dictionary, string name)
        {
            foreach (var record in dictionary)
            {
                var domain = record.Key;
                var expiration = record.Value;
                if (expiration.IsNotExpiringIn(ExpireTestingStatusAfter))
                {
                    continue;
                }
                Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"{name} record for [{domain}] is expiring in [{(expiration.ExpiresAt - DateTime.Now).ToString()}] " +
                        $" start to re-test domain");
                Test(new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN));
            }
        }

        private static void PrepareConfigFiles(out string rogueFile, out string nonRogueFile, out string cannotDetermineFile)
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly == null)
            {
                throw new ApplicationException("Cannot locate Entry Assembly, this library may be called incorrectly");
            }
            var appDirectory = Path.GetDirectoryName(assembly.Location);
            if (string.IsNullOrEmpty(appDirectory))
            {
                throw new ApplicationException("Cannot locate Entry Assembly file location, this library may be called incorrectly");
            }
            var configDirectory = Path.Combine(appDirectory, "config");
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            rogueFile = Path.Combine(configDirectory, "ar-rogues");
            nonRogueFile = Path.Combine(configDirectory, "ar-non-rogues");
            cannotDetermineFile = Path.Combine(configDirectory, "ar-cannot-determine");
        }
        
        private static void PrepareListFiles(out string whiteListFile, out string blackListFile, out string blockListFile)
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly == null)
            {
                throw new ApplicationException("Cannot locate Entry Assembly, this library may be called incorrectly");
            }
            var appDirectory = Path.GetDirectoryName(assembly.Location);
            if (string.IsNullOrEmpty(appDirectory))
            {
                throw new ApplicationException("Cannot locate Entry Assembly file location, this library may be called incorrectly");
            }
            var configDirectory = Path.Combine(appDirectory, "config");
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            whiteListFile = Path.Combine(configDirectory, "ar-white-list");
            blackListFile = Path.Combine(configDirectory, "ar-black-list");
            blockListFile = Path.Combine(configDirectory, "ar-block-list");
        }

        private static void LoadFromFiles()
        {
            PrepareConfigFiles(
                out var rogueFile, 
                out var nonRogueFile, 
                out var cannotDetermineFile);
            PrepareListFiles(
                out var whiteListFile, 
                out var blackListFile,
                out var blockListFile);
            LoadFromFile(rogueFile, Rogues, "ar-rogue");
            LoadFromFile(nonRogueFile, NonRogues, "ar-non-rogue");
            LoadFromFile(cannotDetermineFile, CannotDetermine, "ar-cannot-determine");
            LoadList(whiteListFile, WhiteList, "white-list");
            LoadList(blackListFile, BlackList, "black-list");
            LoadList(blockListFile, BlockList, "block-list");
        }

        private static void LoadList(string file, ICollection<WildcardDomainRecord> list, string name)
        {
            if (File.Exists(file))
            {
                using (var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    var reader = new StreamReader(fileStream, Encoding.UTF8);
                    var parsedRecords = 0;
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine()?.Trim();
                        if (string.IsNullOrEmpty(line))
                        {
                            continue;
                        }
                        if (line[0] == '#')
                        {
                            continue;
                        }
                        list.Add(new WildcardDomainRecord(line));
                        parsedRecords++;
                    }
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Loaded {parsedRecords} {name} records from {file}");
                }
            }
            else
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"{file} not exist, skipped loading {name} records");
            }
        }

        private static void LoadFromFile(string file, ConcurrentDictionary<string, Expiration> dictionary, string name)
        {
            if (File.Exists(file))
            {
                using (var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    var reader = new StreamReader(fileStream, Encoding.UTF8);
                    var parsingLine = 0;
                    var parsedRecords = 0;
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine()?.Trim();
                        parsingLine++;
                        if (string.IsNullOrEmpty(line))
                        {
                            continue;
                        }
                        if (line[0] == '#')
                        {
                            continue;
                        }
                        try
                        {
                            var parts = line.Split(new[] {Splitter}, StringSplitOptions.RemoveEmptyEntries);
                            var domain = parts[0];
                            var expires = DateTime.FromFileTime(long.Parse(parts[1]));
                            dictionary[domain] = new Expiration() {ExpiresAt = expires};
                            parsedRecords++;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(
                                value: $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                                       $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                       $"Failed to parse line {parsingLine} from {file}: [{line}], possible incorrect format.");
                            Console.WriteLine(e);
                        }
                    }
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Loaded {parsedRecords} {name} records from {file}");
                }
            }
            else
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"{file} not exist, skipped loading {name} records");
            }
        }
        
        private static void SaveToFiles()
        {
            PrepareConfigFiles(
                out var rogueFile, 
                out var nonRogueFile, 
                out var cannotDetermineFile);
            SaveToFile(Rogues, rogueFile, "rogue");
            SaveToFile(NonRogues, nonRogueFile, "non-rogues");
            SaveToFile(CannotDetermine, cannotDetermineFile, "cannot-determine");
        }
        
        private static void SaveToFile(ConcurrentDictionary<string, Expiration> dictionary, string file, string name)
        {
            if (UpdateAndSaveTime[dictionary].IsDirty)
            {
                var recordCount = 0;
                using (var memoryStream = new MemoryStream())
                {
                    var writer = new StreamWriter(memoryStream, Encoding.UTF8);
                    foreach (var record in dictionary)
                    {
                        var domain = record.Key;
                        var expires = record.Value.ExpiresAt.ToFileTime().ToString();
                        writer.WriteLine($"{domain}{Splitter}{expires}");
                        recordCount++;
                    }
                    writer.Flush();
                    memoryStream.Position = 0;
                    using (var fileStream = new FileStream(file, FileMode.Create, FileAccess.Write))
                    {
                        memoryStream.CopyTo(fileStream);
                        fileStream.Flush();
                    }
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Saved {recordCount} {name} records to {file}");
                }
            }
            else
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Skipped updating {file} as {name} records not changed");
            }
        }

        private static void StartToTestDomain(DnsQuestionRecord dnsQuestion)
        {
            var domain = dnsQuestion.Name;
            var testResult = GetTestResult(dnsQuestion, false);

            switch (testResult)
            {
                case RogueResult.Rogue:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is marked as being rogue and will not be tested before " +
                        $"[{Rogues[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
                case RogueResult.NotRogue:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is marked as not rogue and will not be tested before " +
                        $"[{NonRogues[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
                case RogueResult.CannotDetermine:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is marked as cannot determined and will not be tested before " +
                        $"[{CannotDetermine[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
                case RogueResult.Testing:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is still being tested and is expected to finish by " +
                        $"[{Testings[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
            }
            Task.Run(() =>
            {
                Test(dnsQuestion);
            });
            MarkDomain(domain, RogueResult.Testing);
        }

        private static void Test(DnsQuestionRecord questionRecord)
        {
            var domain = questionRecord.Name;

            if (_testingForwarders == null || ! _dnsResolverConfig.HasValue)
            {
                Console.WriteLine("**********************************************************************************");
                Console.WriteLine($"{DateTime.Now.ToString(DateTimeFormatter)} " +
                                  $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"TestingForwarders or DnsResolvingConfig not assigned, need check code for error!");
                Console.WriteLine("**********************************************************************************");
                return;
            }
            
            if (IsListed(domain))
            {
                return;
            }
            
            var results = new LinkedList<HttpsTestResult>();
            for (var i = 0; i < TestRounds; i++)
            {
                foreach (var forwarder in _testingForwarders)
                {
                    var dnsClient = new DnsClient(forwarder)
                    {
                        Proxy = _dnsResolverConfig.Value.Proxy,
                        PreferIPv6 = _dnsResolverConfig.Value.PreferIPv6,
                        Protocol = _dnsResolverConfig.Value.ForwarderProtocol,
                        Retries = _dnsResolverConfig.Value.Retries,
                        Timeout = _dnsResolverConfig.Value.Timeout
                    };
                    var response = dnsClient.Resolve(questionRecord);
                    if (response == null)
                    {
                        continue;
                    }

                    foreach (var record in response.Answer)
                    {
                        if (record.RDLENGTH != "4 bytes")
                        {
                            continue;
                        }

                        var ipAddress = record.RDATA.ToString();
                        if (string.IsNullOrEmpty(ipAddress))
                        {
                            continue;
                        }

                        var tcpClient = new TcpClient();
                        var result = tcpClient.BeginConnect(ipAddress, HttpsPort, null, null);
                        var waitHandle = result.AsyncWaitHandle;
                        var success = waitHandle.WaitOne(TimeSpan.FromMilliseconds(TcpConnectTimeoutInMilli), false);
                        if (!success)
                        {
                            results.AddLast(HttpsTestResult.FailedToConnect);
                            continue;
                        }

                        try
                        {
                            tcpClient.EndConnect(result);
                        }
                        catch (Exception)
                        {
                            results.AddLast(HttpsTestResult.FailedToConnect);
                            continue;
                        }

                        try
                        {
                            var sslStream = new SslStream(
                                tcpClient.GetStream(),
                                false,
                                (o, certificate, chain, errors) => errors == SslPolicyErrors.None,
                                null);
                            sslStream.AuthenticateAsClient(domain);
                            results.AddLast(HttpsTestResult.AuthenticationSuccess);
                        }
                        catch (Exception)
                        {
                            results.AddLast(HttpsTestResult.AuthenticationFailed);
                        }
                        finally
                        {
                            tcpClient.Close();
                        }
                    }
                }
            }

            var resultsArray = results.ToArray();
            if (resultsArray.Length == 0)
            {
                MarkDomain(domain, RogueResult.CannotDetermine);
            }
            else if (resultsArray.Any(r => r == HttpsTestResult.AuthenticationSuccess))
            {
                if (resultsArray.Any(r => r == HttpsTestResult.AuthenticationFailed) ||
                    resultsArray.Any(r => r == HttpsTestResult.FailedToConnect))
                {
                    MarkDomain(domain, RogueResult.Rogue);
                }
                else
                {
                    MarkDomain(domain, RogueResult.NotRogue);
                }
            }
            else
            {
                MarkDomain(domain, RogueResult.CannotDetermine);
            }
        }

        private static bool IsListed(string domain)
        {
            var matchedWhiteListRecord = WhiteList.FirstOrDefault(r => r.Dominates(domain));
            if (matchedWhiteListRecord != null)
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Skipped testing [{domain}] as it is white listed by parent domain " +
                    $"[{matchedWhiteListRecord.WildcardDomainName}]");
                MarkDomain(domain, RogueResult.NotRogue);
                return true;
            }
            var matchedBlackListRecord = BlackList.FirstOrDefault(r => r.Dominates(domain));
            if (matchedBlackListRecord != null)
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Skipped testing [{domain}] as it is black listed by parent domain " +
                    $"[{matchedBlackListRecord.WildcardDomainName}]");
                MarkDomain(domain, RogueResult.Rogue);
                return true;
            }

            return false;
        }
        
        private static void AddRogue(string domain)
        {
            if (Rogues.ContainsKey(domain))
            {
                lock (Rogues[domain])
                {
                    Rogues[domain].ExpiresAt = DateTime.Now.Add(ExpireRogueAfter);
                }
                lock (UpdateAndSaveTime[Rogues])
                {
                    UpdateAndSaveTime[Rogues].UpdatedAt = DateTime.Now;
                }
            }
            else
            {
                Rogues[domain] = new Expiration()
                {
                    ExpiresAt = DateTime.Now.Add(ExpireRogueAfter)
                };
                lock (UpdateAndSaveTime[Rogues])
                {
                    UpdateAndSaveTime[Rogues].UpdatedAt = DateTime.Now;
                }
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Marked [{domain}] as rogue for {ExpireRogueAfter.ToString()} until " +
                $"[{Rogues[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void AddNonRogue(string domain)
        {
            if (NonRogues.ContainsKey(domain))
            {
                lock (NonRogues[domain])
                {
                    NonRogues[domain].ExpiresAt = DateTime.Now.Add(ExpireNonRogueAfter);
                }
                lock (UpdateAndSaveTime[NonRogues])
                {
                    UpdateAndSaveTime[NonRogues].UpdatedAt = DateTime.Now;
                }
            }
            else
            {
                NonRogues[domain] = new Expiration()
                {
                    ExpiresAt = DateTime.Now.Add(ExpireNonRogueAfter)
                };
                lock (UpdateAndSaveTime[NonRogues])
                {
                    UpdateAndSaveTime[NonRogues].UpdatedAt = DateTime.Now;
                }
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Marked [{domain}] as non-rogue for {ExpireNonRogueAfter.ToString()} until " +
                $"[{NonRogues[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void AddCannotDetermine(string domain)
        {
            if (CannotDetermine.ContainsKey(domain))
            {
                lock (CannotDetermine[domain])
                {
                    CannotDetermine[domain].ExpiresAt = DateTime.Now.Add(ExpireCannotDetermineAfter);
                }
                lock (UpdateAndSaveTime[CannotDetermine])
                {
                    UpdateAndSaveTime[CannotDetermine].UpdatedAt = DateTime.Now;
                }
            }
            else
            {
                CannotDetermine[domain] = new Expiration()
                {
                    ExpiresAt = DateTime.Now.Add(ExpireCannotDetermineAfter)
                };
                lock (UpdateAndSaveTime[CannotDetermine])
                {
                    UpdateAndSaveTime[CannotDetermine].UpdatedAt = DateTime.Now;
                }
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Marked [{domain}] as cannot determine for {ExpireCannotDetermineAfter.ToString()} until " +
                $"[{CannotDetermine[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void AddTesting(string domain)
        {
            if (Testings.ContainsKey(domain))
            {
                lock (Testings[domain])
                {
                    Testings[domain].ExpiresAt = DateTime.Now.Add(ExpireTestingStatusAfter);
                }
                lock (UpdateAndSaveTime[Testings])
                {
                    UpdateAndSaveTime[Testings].UpdatedAt = DateTime.Now;
                }
            }
            else
            {
                Testings[domain] = new Expiration()
                {
                    ExpiresAt = DateTime.Now.Add(ExpireTestingStatusAfter)
                };
                lock (UpdateAndSaveTime[Testings])
                {
                    UpdateAndSaveTime[Testings].UpdatedAt = DateTime.Now;
                }
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Starting to test [{domain}] which is expected to finish in {ExpireTestingStatusAfter.ToString()} by " +
                $"[{Testings[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void MarkDomain(string domain, RogueResult result)
        {
            switch (result)
            {
                case RogueResult.Rogue:
                    AddRogue(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                case RogueResult.NotRogue:
                    AddNonRogue(domain);
                    RemoveRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                case RogueResult.CannotDetermine:
                    AddCannotDetermine(domain);
                    RemoveRogueRecord(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                case RogueResult.Testing:
                    AddTesting(domain);
                    RemoveRogueRecord(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    break;
                case RogueResult.NotTested:
                    RemoveRogueRecord(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                default:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Cannot make [{domain}] as [{result.ToString()}], need to fix MarkDomain method");
                    break;
            }
        }

        public static bool IsNotIpv4InternetQuestion(DnsQuestionRecord dnsQuestionRecord)
        {
            return ((dnsQuestionRecord.Type != DnsResourceRecordType.A) ||
                    (dnsQuestionRecord.Class != DnsClass.IN && dnsQuestionRecord.Class != DnsClass.ANY));
        }

        public static bool IsIpv4InternetQuestion(DnsQuestionRecord dnsQuestionRecord)
        {
            return !IsNotIpv4InternetQuestion(dnsQuestionRecord);
        }

        private static void RemoveRogueRecord(string domain)
        {
            if (!Rogues.ContainsKey(domain)) return;
            Rogues.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[Rogues])
            {
                UpdateAndSaveTime[Rogues].UpdatedAt = DateTime.Now;
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Removed [{domain}] from rogue which expires at [{expired.ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void RemoveNonRogueRecord(string domain)
        {
            if (!NonRogues.ContainsKey(domain)) return;
            NonRogues.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[NonRogues])
            {
                UpdateAndSaveTime[NonRogues].UpdatedAt = DateTime.Now;
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Removed [{domain}] from non-rogue which expires at [{expired.ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void RemoveCannotDetermineRecord(string domain)
        {
            if (!CannotDetermine.ContainsKey(domain)) return;
            CannotDetermine.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[CannotDetermine])
            {
                UpdateAndSaveTime[CannotDetermine].UpdatedAt = DateTime.Now;
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Removed [{domain}] from cannot determine which expires at [{expired.ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
        }
        
        private static void RemoveTestingRecord(string domain)
        {
            if (!Testings.ContainsKey(domain)) return;
            Testings.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[Testings])
            {
                UpdateAndSaveTime[Testings].UpdatedAt = DateTime.Now;
            }
            Console.WriteLine(
                $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Finished testing [{domain}] which was expected to finish by " +
                $"[{expired.ExpiresAt.ToString(CultureInfo.CurrentCulture)}] "
                + $"(Time taken: {(ExpireTestingStatusAfter - (expired.ExpiresAt-DateTime.Now)).ToString()})"
            );
        }
        
        public static void SetConfig(NameServerAddress[] forwarders, DnsResolverConfig dnsResolvingConfig)
        {
            if (_testingForwarders == null)
            {
                lock (ConfigUpdateLock)
                {
                    _testingForwarders = forwarders;
                }
            }

            if (_dnsResolverConfig == null)
            {
                lock (ConfigUpdateLock)
                {
                    _dnsResolverConfig = dnsResolvingConfig;
                }
            }
        }
        
        public static RogueResult GetTestResult(DnsQuestionRecord dnsQuestion, bool autoStartTesting = true)
        {
            var domain = dnsQuestion.Name;
            if (IsNotIpv4InternetQuestion(dnsQuestion))
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"DNS question for [{domain}  {dnsQuestion.Type.ToString()}  {dnsQuestion.Class.ToString()}] " +
                    $"is not supported for testing or resolving");
                return RogueResult.Blocking;
            }
            
            if (BlockList.Any(item => item.Dominates(domain)))
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"[{domain}] is BLOCKED");
                return RogueResult.Blocking;
            }
            
            if (BlackList.Any(item => item.Dominates(domain)))
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"[{domain}] is BLACK listed");
                return RogueResult.Rogue;
            }

            if (WhiteList.Any(item => item.Dominates(domain)))
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString(DateTimeFormatter)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"[{domain}] is WHITE listed");
                return RogueResult.NotRogue;
            }

            if (Rogues.ContainsKey(domain))
            {
                if (Rogues[domain].IsNotExpired)
                {
                    return RogueResult.Rogue;
                }
                RemoveRogueRecord(domain);
            }

            if (NonRogues.ContainsKey(domain))
            {
                if (NonRogues[domain].IsNotExpired)
                {
                    return RogueResult.NotRogue;
                }
                RemoveNonRogueRecord(domain);
            }

            if (CannotDetermine.ContainsKey(domain))
            {
                if (CannotDetermine[domain].IsNotExpired)
                {
                    return RogueResult.CannotDetermine;
                }
                RemoveCannotDetermineRecord(domain);
            }

            if (Testings.ContainsKey(domain))
            {
                if (Testings[domain].IsNotExpired)
                {
                    return RogueResult.Testing;
                }
                RemoveTestingRecord(domain);
            }

            if (autoStartTesting)
            {
                StartToTestDomain(dnsQuestion);
            }

            return RogueResult.NotTested;
            
        }
    }
}