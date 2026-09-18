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

    public async Task<bool> SyncAppAsync(string appName, string baseUrl, Action<string>? onProgress = null)
    {
        try
        {
            onProgress?.Invoke($"[{appName}] Получаем список файлов...");

            var assetUrls = await GetAssetListFromSwAsync(baseUrl);
            if (assetUrls.Count == 0)
            {
                onProgress?.Invoke($"[{appName}] sw.js без ASSETS — парсим index.html");
                assetUrls = await GetAssetListFromIndexAsync(baseUrl);
            }

            if (assetUrls.Count == 0)
            {
                onProgress?.Invoke($"[{appName}] Не удалось получить список файлов");
                return false;
            }

            if (!assetUrls.Any(u => u.EndsWith("index.html")))
                assetUrls.Insert(0, "index.html");

            assetUrls = assetUrls.Distinct().ToList();
            onProgress?.Invoke($"[{appName}] Найдено файлов: {assetUrls.Count}");

            var tempPath = Path.Combine(_cacheRoot, appName + "_tmp");
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
            Directory.CreateDirectory(tempPath);

            int downloaded = 0, failed = 0;

            foreach (var assetUrl in assetUrls)
            {
                try
                {
                    var cleanUrl = NormalizeAssetUrl(assetUrl);
                    if (string.IsNullOrWhiteSpace(cleanUrl)) continue;

                    var fullUrl = CombineUrl(baseUrl, cleanUrl);
                    var content = await _http.GetByteArrayAsync(fullUrl);

                    var localPath = Path.Combine(tempPath, cleanUrl);
                    var localDir = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(localDir))
                        Directory.CreateDirectory(localDir);

                    await File.WriteAllBytesAsync(localPath, content);
                    downloaded++;

                    if (downloaded % 10 == 0)
                        onProgress?.Invoke($"[{appName}] {downloaded}/{assetUrls.Count}");
                }
                catch (Exception ex)
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

            var finalPath = GetAppCachePath(appName);
            var backupPath = finalPath + "_bak";

            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);
            if (Directory.Exists(finalPath)) Directory.Move(finalPath, backupPath);

            Directory.Move(tempPath, finalPath);

            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);

            onProgress?.Invoke($"[{appName}] Готово");
            return true;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"[{appName}] Ошибка: {ex.Message}");
            return false;
        }
    }

    private async Task<List<string>> GetAssetListFromSwAsync(string baseUrl)
    {
        var result = new List<string>();
        try
        {
            var swUrl = baseUrl.TrimEnd('/') + "/sw.js";
            var swContent = await _http.GetStringAsync(swUrl);

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

    private async Task<List<string>> GetAssetListFromIndexAsync(string baseUrl)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var indexUrl = baseUrl.TrimEnd('/') + "/index.html";
            var html = await _http.GetStringAsync(indexUrl);

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
        url = url.TrimStart('.').TrimStart('/');
        url = url.Replace('\\', '/');
        return url;
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