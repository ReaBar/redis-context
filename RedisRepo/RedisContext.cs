﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using StackExchange.Redis;

namespace Payoneer.Infra.RedisRepo
{
    public class RedisContext : IRedisContext
    {
        private const int DefaultTotalConnections = 2;

        private readonly string contextNamespace;
        
        private readonly int databaseNumber;
        private readonly List<string> hosts;
        private readonly ILogger log;
        private readonly int defaultRetries = 5;

        private IConnectionMultiplexer[] connections;

        internal readonly CommandFlags commandFlags;
        private readonly int totalConnections;

        public RedisContext(
            string contextNamespace,
            string host = "localhost", int port = 6379, string password = null, int db = 0)
            : this(
                contextNamespace,
                ToConnectionString(host, port, password, db),
                commandFlags: CommandFlags.None, defaultRetries: 5, totalConnections: DefaultTotalConnections)
        {
        }

        public RedisContext(
            string contextNamespace,
            string host, int port, string password, int db,
            CommandFlags commandFlags, int defaultRetries)
            : this(
                contextNamespace,
                ToConnectionString(host, port, password, db),
                commandFlags, defaultRetries, totalConnections: DefaultTotalConnections)
        {
        }

        public RedisContext(
            string contextNamespace,
            string host, int port, string password, int db,
            CommandFlags commandFlags, int defaultRetries, int totalConnections)
             : this(
                contextNamespace,
                ToConnectionString(host, port, password, db),
                commandFlags, defaultRetries, totalConnections)
        {
        }

        public RedisContext(string contextNamespace, string connectionString)
            : this(
                contextNamespace, connectionString, commandFlags: CommandFlags.None, defaultRetries: 5, totalConnections: DefaultTotalConnections)
        {
        }

        public RedisContext(
            string contextNamespace, string connectionString,
            CommandFlags commandFlags, int defaultRetries,
            int totalConnections)
        {
            this.log = LogManager.GetLogger(typeof(RedisContext).FullName);
            this.contextNamespace = contextNamespace;
            var connectionOptions = ToConnectionOptions(connectionString);
            this.databaseNumber = connectionOptions.RedisConfigurationOptions.DefaultDatabase ?? 0;
            this.hosts = connectionOptions.Hosts;
            this.commandFlags = commandFlags;
            this.totalConnections = totalConnections;
            InitConnections(connectionOptions);
        }

        private void InitConnections(RedisConnectionOptions options)
        {
            if (this.totalConnections < 1)
            {
                throw new ArgumentException("total connections must be greater than 0", nameof(this.totalConnections));
            }

            this.connections = new IConnectionMultiplexer[this.totalConnections];
            for (int i = 0; i < totalConnections; i++)
            {
               this.connections[i]  = ConnectionMultiplexer.Connect(options.RedisConfigurationOptions);
                this.connections[i].PreserveAsyncOrder = false;
            }
        }



        #region Properties

        private int connectionIndex = 0;
        protected IConnectionMultiplexer Connection
        {
            get
            {
                var result = this.connections[connectionIndex];
                connectionIndex = (connectionIndex + 1) % totalConnections;
                return result;
            }
        }

        protected int DatabaseNumber => databaseNumber;

        internal protected virtual IDatabase Database => this.Connection.GetDatabase(db: this.databaseNumber);

        #endregion

        #region ConnectionString

        protected class RedisConnectionOptions
        {
            public ConfigurationOptions RedisConfigurationOptions { get; set; }
            public List<string> Hosts { get; set; }
        }

        private static RedisConnectionOptions ToConnectionOptions(string connectionString)
        {
            const string prefix = @"redis://";
            if (string.IsNullOrEmpty(connectionString))
                connectionString = @"redis://localhost:6379";

            var queryIndex = connectionString.IndexOf('?');

            string hosts = connectionString, queryString = null;

            if (queryIndex >= 0)
            {
                queryString = connectionString.Substring(queryIndex);
                hosts = connectionString.Substring(0, queryIndex);
            }

            if (hosts.ToLowerInvariant().StartsWith(prefix))
                hosts = hosts.Substring(prefix.Length);

            string userInfo = null;

            var atIndex = hosts.IndexOf('@');
            if (atIndex >= 0)
            {
                userInfo = hosts.Substring(0, atIndex);
                if (userInfo.Length == 0)
                    userInfo = null;

                hosts = hosts.Substring(atIndex + 1);
            }

            var hostNames = hosts.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (hostNames.Length == 0)
                hostNames = new[] { "localhost" };

            var arguments = !string.IsNullOrEmpty(queryString)
                ? ParseQuery(queryString)
                : new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(userInfo))
                arguments[nameof(ConfigurationOptions.Password).ToLowerInvariant()] = userInfo;

            var database = GetValue(arguments, nameof(ConfigurationOptions.DefaultDatabase), 0);

            var cfg = new ConfigurationOptions
            {
                AbortOnConnectFail = GetValue(arguments, nameof(ConfigurationOptions.AbortOnConnectFail), false),
                AllowAdmin = GetValue(arguments, nameof(ConfigurationOptions.AllowAdmin), false),
                ConnectRetry = GetValue(arguments, nameof(ConfigurationOptions.ConnectRetry), 2),
                ConnectTimeout = GetValue(arguments, nameof(ConfigurationOptions.ConnectTimeout), 5000),
                ClientName = GetValue(arguments, nameof(ConfigurationOptions.ClientName), null),
                DefaultDatabase = database,
                KeepAlive = GetValue(arguments, nameof(ConfigurationOptions.KeepAlive), -1),
                Password = GetValue(arguments, nameof(ConfigurationOptions.Password), null),
                ResolveDns = GetValue(arguments, nameof(ConfigurationOptions.ResolveDns), false),
                SyncTimeout = GetValue(arguments, nameof(ConfigurationOptions.SyncTimeout), 1000),
                ServiceName = GetValue(arguments, nameof(ConfigurationOptions.ServiceName), null),
                WriteBuffer = GetValue(arguments, nameof(ConfigurationOptions.WriteBuffer), 4096),
                Ssl = GetValue(arguments, nameof(ConfigurationOptions.Ssl), false),
                SslHost = GetValue(arguments, nameof(ConfigurationOptions.SslHost), null),
                ConfigurationChannel = GetValue(arguments, nameof(ConfigurationOptions.ConfigurationChannel), null),
                TieBreaker = GetValue(arguments, nameof(ConfigurationOptions.TieBreaker), null),

            };
            cfg.ResponseTimeout = GetValue(arguments, nameof(ConfigurationOptions.ResponseTimeout), cfg.SyncTimeout);

            var endPoints = cfg.EndPoints;
            foreach (var hostName in hostNames)
                endPoints.Add(hostName);

            return new RedisConnectionOptions
            {
                Hosts = hostNames.ToList(),
                RedisConfigurationOptions = cfg
            };
        }

        private static string ToConnectionString(string host = "localhost", int port = 6379, string password = null, int db = 0)
        {
            var userInfo = !string.IsNullOrEmpty(password) ? password + '@' : string.Empty;
            return $"{userInfo}{host}:{port}?defaultdatabase={db}";
        }

        private static string GetValue(Dictionary<string, string> arguments, string argumentName, string defaultValue)
        {
            if (arguments.TryGetValue(argumentName.ToLowerInvariant(), out string value))
                return value;
            return defaultValue;
        }

        private static int GetValue(Dictionary<string, string> arguments, string argumentName, int defaultValue)
        {
            if (arguments.TryGetValue(argumentName.ToLowerInvariant(), out string s)
                && int.TryParse(s, out int value))
            {
                return value;
            }

            return defaultValue;
        }

        private static bool GetValue(Dictionary<string, string> arguments, string argumentName, bool defaultValue)
        {
            if (arguments.TryGetValue(argumentName.ToLowerInvariant(), out string s)
                && bool.TryParse(s, out bool value))
            {
                return value;
            }

            return defaultValue;
        }

        private static Dictionary<string, string> ParseQuery(string uriQuery)
        {
            var arguments = uriQuery
                .Substring(1) // Remove '?'
                .Split('&')
                .Select(q =>
                {
                    var kvArray = q.Split('=');
                    if (kvArray.Length == 2)
                        return new KeyValuePair<string, string>(kvArray[0], kvArray[1]);
                    return (KeyValuePair<string, string>?)null;
                })
                .Where(kv => kv.HasValue)
                .GroupBy(kv => kv.Value.Key)
                .ToDictionary(g => g.Key.ToLowerInvariant(), g => g.First()?.Value);

            return arguments;
        }

        #endregion

        #region retries
        public static TResult Retry<TResult>(Func<TResult> func, int maxAttempts)
        {
            return RetryUtil.Retry(func, maxAttempts);
        }
        public static void Retry(Action action, int maxAttempts)
        {
            RetryUtil.Retry(action, maxAttempts);
        }
        #endregion

        public virtual string Key(string key)
        {
            return !string.IsNullOrEmpty(contextNamespace)
                ? string.Concat("ns=", contextNamespace, ":k=", key)
                : key;
        }
        
        #region TryGet

        public bool TryGet(string key, out string value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToDotNetString(out value);
        }

        public bool TryGet(string key, out int? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToNullableInt(out value);
        }

        public bool TryGet(string key, out int value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToInt(out value);
        }

        public bool TryGet(string key, out long? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToNullableLong(out value);
        }

        public bool TryGet(string key, out long value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToLong(out value);
        }

        public bool TryGet(string key, out double? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToNullableDouble(out value);
        }

        public bool TryGet(string key, out double value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToDouble(out value);
        }

        public bool TryGet(string key, out bool? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);
            return redisValue.ToNullableBool(out value);
        }

        public bool TryGet(string key, out bool value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGet(Key(key), flags: commandFlags), defaultRetries);

            return redisValue.ToBool(out value);
        }

        #endregion

        #region Set (Set value)

        public void Set(string key, string value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, bool value, TimeSpan? expiry = null)
        {
            var intValue = value ? -1 : 0;
            Retry(() =>
                this.Database.StringSet(Key(key), intValue, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, bool? value, TimeSpan? expiry = null)
        {
            var intValue = value.HasValue ? (value.Value ? -1 : 0) : (int?)null;
            Retry(() =>
                this.Database.StringSet(Key(key), intValue, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, double value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, double? value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, int value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, int? value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, long value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        public void Set(string key, long? value, TimeSpan? expiry = null)
        {
            Retry(() =>
                this.Database.StringSet(Key(key), value, expiry: expiry, flags: commandFlags), defaultRetries);
        }

        #endregion

        #region Delete

        public void Delete(string key)
        {
            Retry(() =>
                this.Database.KeyDelete(Key(key), flags: commandFlags), defaultRetries);
        }

        public void Delete(params string[] keys)
        {
            Retry(() =>
                this.Database.KeyDelete(keys.Select(k => (RedisKey)Key(k)).ToArray(), flags: commandFlags), defaultRetries);
        }

        #endregion

        #region SetOrAppend

        public void SetOrAppend(string key, string value)
        {
            Retry(() => this.Database.StringAppend(Key(key), value, flags: commandFlags), RetryUtil.NoRetries);
        }

        #endregion

        #region Increment

        public long Increment(string key, long value)
        {
            return Retry(() => this.Database.StringIncrement(Key(key), value, flags: commandFlags), RetryUtil.NoRetries);
        }

        public double Increment(string key, double value)
        {
            return Retry(() => this.Database.StringIncrement(Key(key), value, flags: commandFlags), RetryUtil.NoRetries);
        }

        #endregion

        #region Decrement

        public long Decrement(string key, long value)
        {
            return Retry(() => this.Database.StringDecrement(Key(key), value, flags: commandFlags), RetryUtil.NoRetries);
        }

        public double Decrement(string key, double value)
        {
            return Retry(() => this.Database.StringDecrement(Key(key), value, flags: commandFlags), RetryUtil.NoRetries);
        }

        #endregion

        #region AtomicExchange

        public string AtomicExchange(string key, string value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToDotNetString(out string previousValue) ? previousValue : default(string);
        }

        public int? AtomicExchange(string key, int? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToNullableInt(out int? previousValue) ? previousValue : default(int?);
        }

        public int AtomicExchange(string key, int value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToInt(out int previousValue) ? previousValue : default(int);
        }

        public long? AtomicExchange(string key, long? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToNullableLong(out long? previousValue) ? previousValue : default(long?);
        }

        public long AtomicExchange(string key, long value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToLong(out long previousValue) ? previousValue : default(long);
        }

        public double? AtomicExchange(string key, double? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToNullableDouble(out double? previousValue) ? previousValue : default(double?);
        }

        public double AtomicExchange(string key, double value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToDouble(out double previousValue) ? previousValue : default(double);
        }

        public bool? AtomicExchange(string key, bool? value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToNullableBool(out bool? previousValue) ? previousValue : default(bool?);
        }

        public bool AtomicExchange(string key, bool value)
        {
            var redisValue = Retry(() =>
                this.Database.StringGetSet(Key(key), (RedisValue)value, flags: commandFlags), RetryUtil.NoRetries);
            return redisValue.ToBool(out bool previousValue) && previousValue;
        }

        #endregion

        #region TimeToLive

        public TimeSpan? GetTimeToLive(string key)
        {
            return Retry(() => this.Database.KeyTimeToLive(Key(key), flags: commandFlags), defaultRetries);
        }

        public void SetTimeToLive(string key, TimeSpan? expiry)
        {
            var keyExists = Retry(() => this.Database.KeyExists(Key(key), flags: commandFlags), defaultRetries);

            // If key in DB
            // If expiry was requested, then update
            // If no expiry was requested, then update only if there is currently an expiry set
            if (keyExists && (expiry.HasValue || GetTimeToLive(Key(key)).HasValue))
            {
                Retry(() => this.Database.KeyExpire(Key(key), expiry, flags: commandFlags), defaultRetries);
            }
        }

        #endregion

        #region GetKeys

        /// <summary>
        /// Do not use in production!
        /// </summary>
        /// <param name="pattern"></param>
        /// <returns></returns>
        public IEnumerable<string> GetKeys(string pattern = null)
        {
            var keys = this.Connection.GetServer(hosts.First())
                .Keys(this.databaseNumber, Key(pattern)).ToList()
                .Select(rk => rk.ToString()).ToList();

            if (string.IsNullOrEmpty(contextNamespace))
                return keys;

            var keyPrefixLength = Key(string.Empty).Length;
            var keysWithoutPrefix = keys.Select(k => k.Substring(keyPrefixLength)).ToList();
            return keysWithoutPrefix;
        }

        #endregion

        #region IDisposable
        public void Dispose()
        {
            if (this.connections != null)
            {
                foreach (var conn in this.connections)
                {
                    conn?.Dispose();
                }
            }
        }

        #endregion

    }
}
