using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace EndpointConnectionApp
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // Uruchomienie asynchronicznej logiki w synchronnym Main
            RunAsync().GetAwaiter().GetResult();
        }

        private static async Task RunAsync()
        {
            // Wczytaj konfigurację
            var config = Configuration.AppConfig.Load("appsettings.json");

            // Prosty kontener DI
            var container = new SimpleDI.SimpleContainer();

            // Loggery
            var programLogger = new Logging.ConsoleLogger<Program>();
            var serviceLogger = new Logging.ConsoleLogger<Services.CatFactService>();
            var fileLoggerConsole = new Logging.ConsoleLogger<Services.FileLogger>();

            // HttpClient z timeout z konfiguracji
            var httpClient = new System.Net.Http.HttpClient();
            if (config.RequestTimeoutSeconds > 0)
                httpClient.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);

            // Rejestracja zależności w prostym kontenerze
            container.RegisterSingleton<System.Net.Http.HttpClient>(() => httpClient);
            container.RegisterSingleton<Logging.ILogger<Program>>(() => programLogger);
            container.RegisterSingleton<Logging.ILogger<Services.CatFactService>>(() => serviceLogger);
            container.RegisterSingleton<Logging.ILogger<Services.FileLogger>>(() => fileLoggerConsole);
            container.RegisterSingleton<Services.ICatFactService>(() => new Services.CatFactService(httpClient, config, serviceLogger));
            container.RegisterSingleton<Services.IFileLogger>(() => new Services.FileLogger(config.FilePath, fileLoggerConsole));

            var factService = container.GetService<Services.ICatFactService>();
            var fileLogger = container.GetService<Services.IFileLogger>();

            programLogger.LogInformation("Aplikacja uruchomiona.");
            Console.WriteLine("Aplikacja pobierająca fakty o kotach z: " + config.BaseUrl);
            Console.WriteLine("Naciśnij Enter aby pobrać fakt i dopisać go do pliku. Naciśnij Esc aby zakończyć.");

            while (true)
            {
                var keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.Escape)
                    break;

                if (keyInfo.Key != ConsoleKey.Enter)
                    continue;

                // CancellationToken dla pojedynczego żądania
                using (var cts = new CancellationTokenSource())
                {
                    if (config.RequestTimeoutSeconds > 0)
                        cts.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds + 2));

                    try
                    {
                        programLogger.LogInformation("Request started");
                        var fact = await factService.GetRandomFactAsync(cts.Token).ConfigureAwait(false);
                        if (fact == null || string.IsNullOrWhiteSpace(fact.Fact))
                        {
                            programLogger.LogWarning("Pusty lub nieprawidłowy response od API");
                            Console.WriteLine("Otrzymano pusty lub nieprawidłowy response od API.");
                            continue;
                        }

                        programLogger.LogInformation("Fact received: " + fact.Fact);

                        // Zapisujemy tylko treść faktu zgodnie z wymaganiem (po jednej linii = sam tekst faktu)
                        var line = fact.Fact ?? string.Empty;

                        try
                        {
                            // zapis z tokenem
                            await fileLogger.AppendLineAsync(line, cts.Token).ConfigureAwait(false);
                            programLogger.LogInformation("Fact saved");
                            Console.WriteLine("Zapisano: " + line);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        {
                            programLogger.LogError("File write failed", ex);
                            Console.WriteLine("Pobrano fakt, lecz nie udało się zapisać do pliku: " + ex.Message);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        programLogger.LogWarning("Operacja anulowana/timeout podczas pobierania faktu");
                        Console.WriteLine("Operacja anulowana lub przekroczono limit czasu przy żądaniu API.");
                    }
                    catch (Exception ex)
                    {
                        programLogger.LogError("API request failed", ex);
                        Console.WriteLine("Błąd podczas pobierania faktu: " + ex.Message);
                    }
                }
            }

            programLogger.LogInformation("Koniec programu.");
            Console.WriteLine("Koniec programu.");
        }
    }
}

// ----- Prosty kontener DI -----
namespace SimpleDI
{
    using System;
    using System.Collections.Concurrent;

    public class SimpleContainer
    {
        private readonly ConcurrentDictionary<Type, Func<object>> _factories = new ConcurrentDictionary<Type, Func<object>>();

        public void RegisterSingleton<TService>(Func<TService> factory) where TService : class
        {
            _factories[typeof(TService)] = () => factory();
        }

        public void RegisterTransient<TService>(Func<TService> factory) where TService : class
        {
            _factories[typeof(TService)] = () => factory();
        }

        public TService GetService<TService>() where TService : class
        {
            if (_factories.TryGetValue(typeof(TService), out var f))
                return (TService)f();
            throw new InvalidOperationException("Service not registered: " + typeof(TService).FullName);
        }
    }
}

// ----- Konfiguracja -----
namespace Configuration
{
    using System;
    using System.IO;

    public class AppConfig
    {
        public string BaseUrl { get; set; } = "https://catfact.ninja/fact";
        public string FilePath { get; set; } = "catfacts.txt";
        public int RequestTimeoutSeconds { get; set; } = 10;
        public int RetryMaxAttempts { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 500;

        public static AppConfig Load(string path)
        {
            if (!File.Exists(path))
                return new AppConfig();

            try
            {
                var json = File.ReadAllText(path);
                // bardzo prosty parser - tylko wydobywamy potrzebne wartości
                var cfg = new AppConfig();

                string GetString(string key)
                {
                    var idx = json.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return null;
                    var colon = json.IndexOf(':', idx);
                    if (colon < 0) return null;
                    var firstQuote = json.IndexOf('"', colon + 1);
                    if (firstQuote < 0) return null;
                    var secondQuote = json.IndexOf('"', firstQuote + 1);
                    if (secondQuote < 0) return null;
                    return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                }

                string GetNumber(string key)
                {
                    var idx = json.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return null;
                    var colon = json.IndexOf(':', idx);
                    if (colon < 0) return null;
                    var pos = colon + 1;
                    while (pos < json.Length && (char.IsWhiteSpace(json[pos]) || json[pos] == '"')) pos++;
                    var sb = new System.Text.StringBuilder();
                    while (pos < json.Length && (char.IsDigit(json[pos]) || json[pos] == '-'))
                    {
                        sb.Append(json[pos]); pos++;
                    }
                    return sb.Length > 0 ? sb.ToString() : null;
                }

                var baseUrl = GetString("BaseUrl") ?? GetString("baseUrl");
                if (!string.IsNullOrEmpty(baseUrl)) cfg.BaseUrl = baseUrl;

                var filePath = GetString("Path") ?? GetString("path");
                if (!string.IsNullOrEmpty(filePath)) cfg.FilePath = filePath;

                var timeout = GetNumber("RequestTimeoutSeconds");
                if (int.TryParse(timeout, out var t)) cfg.RequestTimeoutSeconds = t;

                var attempts = GetNumber("RetryMaxAttempts");
                if (int.TryParse(attempts, out var a)) cfg.RetryMaxAttempts = a;

                var delay = GetNumber("RetryDelayMs");
                if (int.TryParse(delay, out var d)) cfg.RetryDelayMs = d;

                return cfg;
            }
            catch
            {
                return new AppConfig();
            }
        }
    }
}

// ----- Logging -----
namespace Logging
{
    using System;

    public interface ILogger<T>
    {
        void LogInformation(string message);
        void LogWarning(string message);
        void LogError(string message, Exception ex = null);
    }

    public class ConsoleLogger<T> : ILogger<T>
    {
        private string Prefix => typeof(T).Name;
        public void LogInformation(string message) => Console.WriteLine($"[INFO] {DateTime.Now:O} [{Prefix}] {message}");
        public void LogWarning(string message) => Console.WriteLine($"[WARN] {DateTime.Now:O} [{Prefix}] {message}");
        public void LogError(string message, Exception ex = null)
        {
            Console.WriteLine($"[ERR ] {DateTime.Now:O} [{Prefix}] {message} {ex?.Message}");
        }
    }

}

// ----- Model i Serwisy -----
namespace Models
{
    public class CatFact
    {
        public string Fact { get; set; }
        public int Length { get; set; }
    }
}

namespace Services
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Models;
    using Configuration;
    using Logging;

    public interface ICatFactService
    {
        Task<CatFact> GetRandomFactAsync(CancellationToken cancellationToken = default);
    }

    public class CatFactService : ICatFactService
    {
        private readonly HttpClient _httpClient;
        private readonly AppConfig _config;
        private readonly ILogger<CatFactService> _logger;

        public CatFactService(HttpClient httpClient, AppConfig config, ILogger<CatFactService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<CatFact> GetRandomFactAsync(CancellationToken cancellationToken = default)
        {
            var attempts = Math.Max(1, _config.RetryMaxAttempts);
            var delay = Math.Max(0, _config.RetryDelayMs);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    _logger.LogInformation($"Request started (attempt {attempt})");
                    using (var resp = await _httpClient.GetAsync(_config.BaseUrl, cancellationToken).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            var code = (int)resp.StatusCode;
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            _logger.LogWarning($"API returned status {(int)resp.StatusCode} - {resp.ReasonPhrase}");
                            if (code >= 500 && attempt < attempts)
                            {
                                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                                continue; // retry
                            }
                            throw new HttpRequestException($"API returned {(int)resp.StatusCode}: {resp.ReasonPhrase}") { Data = { { "Body", body } } };
                        }

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                            throw new InvalidDataException("Empty response from API");

                        // prosty parser
                        var fact = new CatFact();
                        try
                        {
                            var factKey = "\"fact\"";
                            var lengthKey = "\"length\"";
                            var fi = json.IndexOf(factKey, StringComparison.OrdinalIgnoreCase);
                            if (fi >= 0)
                            {
                                var colon = json.IndexOf(':', fi);
                                var firstQuote = json.IndexOf('"', colon + 1);
                                if (firstQuote >= 0)
                                {
                                    var start = firstQuote + 1;
                                    var end = json.IndexOf('"', start);
                                    if (end > start)
                                        fact.Fact = json.Substring(start, end - start);
                                }
                            }

                            var li = json.IndexOf(lengthKey, StringComparison.OrdinalIgnoreCase);
                            if (li >= 0)
                            {
                                var colon = json.IndexOf(':', li);
                                var pos = colon + 1;
                                while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
                                var sb = new StringBuilder();
                                while (pos < json.Length && char.IsDigit(json[pos]))
                                {
                                    sb.Append(json[pos]); pos++;
                                }
                                if (sb.Length > 0 && int.TryParse(sb.ToString(), out var val))
                                    fact.Length = val;
                            }
                        }
                        catch
                        {
                            // ignore parsing errors
                        }

                        if (string.IsNullOrWhiteSpace(fact.Fact))
                            throw new InvalidDataException("Invalid response format: missing 'fact'");

                        _logger.LogInformation("Fact received");
                        return fact;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Operation cancelled or timeout");
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning($"HttpRequestException: {ex.Message}");
                    if (attempt < attempts)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Unexpected error during API call", ex);
                    throw;
                }
            }

            throw new Exception("Unable to get fact after retries");
        }
    }

    public interface IFileLogger
    {
        Task AppendLineAsync(string line, CancellationToken cancellationToken = default);
    }

    public class FileLogger : IFileLogger
    {
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly Logging.ILogger<FileLogger> _logger;

        public FileLogger(string path, Logging.ILogger<FileLogger> logger)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task AppendLineAsync(string line, CancellationToken cancellationToken = default)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    // upewnij się, że katalog istnieje
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }

                _logger.LogInformation("Line appended to file");
                return Task.CompletedTask;
            }
            catch
            {
                _logger.LogError("Failed to write to file", null);
                throw;
            }
        }
    }
}
