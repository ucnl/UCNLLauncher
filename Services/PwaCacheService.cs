using System.Text.RegularExpressions;

namespace UCNLLauncher.Services;

public class PwaCacheService
{
    private readonly string _cacheRoot;
    private readonly HttpClient _http;

    public string CacheRoot => _cacheRoot;

    public PwaCacheService()
    {
        _cacheRoot = Path.Combine(FileSystem.AppDataDirectory, "pwa_cache");
        Directory.CreateDirectory(_cacheRoot);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string GetAppCachePath(string appName)
    {
        var path = Path.Combine(_cacheRoot, appName);
        Directory.CreateDirectory(path);
        return path;
    }

    public bool HasLocalCopy(string appName)
    {
        var indexPath = Path.Combine(GetAppCachePath(appName), "index.html");
        return File.Exists(indexPath);
    }

    public async Task<bool> SyncAppAsync(string appName, string baseUrl, Action<string>? onProgress = null, CancellationToken token = default)
    {
        string? tempPath = null;
        try
        {
            onProgress?.Invoke($"[{appName}] Получаем список файлов...");

            var swAssets = await GetAssetListFromSwAsync(baseUrl, token);
            var indexAssets = await GetAssetListFromIndexAsync(baseUrl, token);
            token.ThrowIfCancellationRequested();
            if (swAssets.Count == 0)
                onProgress?.Invoke($"[{appName}] sw.js без ASSETS — парсим index.html");

            var assetUrls = swAssets.Concat(indexAssets)
                .Select(NormalizeAssetUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (assetUrls.Count == 0)
            {
                onProgress?.Invoke($"[{appName}] Не удалось получить список файлов");
                return false;
            }

            if (!assetUrls.Any(u => u == "index.html"))
                assetUrls.Insert(0, "index.html");

            onProgress?.Invoke($"[{appName}] Найдено файлов: {assetUrls.Count}");

            tempPath = Path.Combine(_cacheRoot, appName + "_tmp");
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
            Directory.CreateDirectory(tempPath);
            var tempRoot = Path.GetFullPath(tempPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            int downloaded = 0, failed = 0;

            foreach (var assetUrl in assetUrls)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var localPath = Path.GetFullPath(Path.Combine(tempPath, assetUrl));
                    if (!localPath.StartsWith(tempRoot, StringComparison.Ordinal))
                    {
                        failed++;
                        onProgress?.Invoke($"[{appName}] Пропущен {assetUrl}: путь вне каталога приложения");
                        continue;
                    }

                    var fullUrl = CombineUrl(baseUrl, assetUrl);
                    var content = await RetryAsync(() => _http.GetByteArrayAsync(fullUrl, token), token);

                    var localDir = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(localDir))
                        Directory.CreateDirectory(localDir);

                    await File.WriteAllBytesAsync(localPath, content, token);
                    downloaded++;

                    if (downloaded % 10 == 0)
                        onProgress?.Invoke($"[{appName}] {downloaded}/{assetUrls.Count}");
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failed++;
                    onProgress?.Invoke($"[{appName}] Пропущен {assetUrl}: {ex.Message}");
                }
            }

            onProgress?.Invoke($"[{appName}] Скачано {downloaded}, пропущено {failed}");

            var indexFile = Path.Combine(tempPath, "index.html");
            if (!File.Exists(indexFile))
            {
                onProgress?.Invoke($"[{appName}] КРИТИЧНО: index.html не скачан");
                Directory.Delete(tempPath, true);
                return false;
            }

            token.ThrowIfCancellationRequested();
            var finalPath = GetAppCachePath(appName);
            var backupPath = finalPath + "_bak";

            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);
            if (Directory.Exists(finalPath)) Directory.Move(finalPath, backupPath);

            Directory.Move(tempPath, finalPath);

            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);

            onProgress?.Invoke($"[{appName}] Готово");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            onProgress?.Invoke($"[{appName}] Отменено");
            TryDeleteDirectory(tempPath);
            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"[{appName}] Ошибка: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    private async Task<List<string>> GetAssetListFromSwAsync(string baseUrl, CancellationToken token)
    {
        var result = new List<string>();
        try
        {
            var swUrl = baseUrl.TrimEnd('/') + "/sw.js";
            var swContent = await RetryAsync(() => _http.GetStringAsync(swUrl, token), token);

            var match = Regex.Match(swContent,
                @"const\s+ASSETS\s*=\s*\[(.*?)\]",
                RegexOptions.Singleline);

            if (!match.Success) return result;

            var arrayContent = match.Groups[1].Value;
            var itemRegex = new Regex(@"['""]([^'""]+)['""]");
            foreach (Match m in itemRegex.Matches(arrayContent))
            {
                var url = m.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (url == "./") continue;
                result.Add(url);
            }
        }
        catch { }
        return result;
    }

    private async Task<List<string>> GetAssetListFromIndexAsync(string baseUrl, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var indexUrl = baseUrl.TrimEnd('/') + "/index.html";
            var html = await RetryAsync(() => _http.GetStringAsync(indexUrl, token), token);

            result.Add("index.html");

            var regex = new Regex(@"(?:src|href)\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);

            foreach (Match m in regex.Matches(html))
            {
                var url = m.Groups[1].Value.Trim();

                if (url.StartsWith("http://") || url.StartsWith("https://")
                    || url.StartsWith("data:") || url.StartsWith("#")
                    || url.StartsWith("mailto:") || url.StartsWith("javascript:"))
                    continue;

                var qIdx = url.IndexOf('?');
                if (qIdx >= 0) url = url.Substring(0, qIdx);
                var hIdx = url.IndexOf('#');
                if (hIdx >= 0) url = url.Substring(0, hIdx);
                if (string.IsNullOrWhiteSpace(url)) continue;

                result.Add(url);
            }
        }
        catch { }
        return result.ToList();
    }

    private static string NormalizeAssetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var qIdx = url.IndexOf('?');
        if (qIdx >= 0) url = url.Substring(0, qIdx);
        var hIdx = url.IndexOf('#');
        if (hIdx >= 0) url = url.Substring(0, hIdx);
        url = url.Replace('\\', '/').Trim();
        while (url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith("/", StringComparison.Ordinal))
            url = url.StartsWith("./", StringComparison.Ordinal) ? url.Substring(2) : url.Substring(1);
        if (url == ".") return "";
        return url;
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                throw;
            }
            catch (Exception) when (attempt < attempts && !token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), token);
            }
        }
    }

    private static string CombineUrl(string baseUrl, string relative)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        return new Uri(baseUri, relative).ToString();
    }

    public void DeleteAppCache(string appName)
    {
        try
        {
            var path = GetAppCachePath(appName);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    public void DeleteAll()
    {
        try
        {
            if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, true);
            Directory.CreateDirectory(_cacheRoot);
        }
        catch { }
    }
}