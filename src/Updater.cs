using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ModbusScan
{
    public class UpdateInfo
    {
        public string Tag;
        public Version Version;
        public bool NewerAvailable;
        public string ReleasePage;
        public string DownloadUrl;
        public long SizeBytes;
        public bool AssetAvailable;
    }

    /// <summary>
    /// GitHub'daki en yeni release'i denetler ve programı kendi dosyasıyla değiştirir.
    /// api.github.com yerine github.com/.../releases/latest yönlendirmesi kullanılır;
    /// böylece dakikada bir kontrol, kimliksiz API hız sınırına (saatte 60) takılmaz.
    /// </summary>
    public static class Updater
    {
        public const string Owner = "dincer552";
        public const string Repo = "modbus_scan";

        private const int RequestTimeoutMs = 8000;

        static Updater()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        }

        public static string LatestReleaseUrl
        {
            get { return "https://github.com/" + Owner + "/" + Repo + "/releases/latest"; }
        }

        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string text = tag.Trim();
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);
            Version parsed;
            if (!Version.TryParse(text, out parsed)) return null;
            return parsed;
        }

        public static string TagFromLocation(string location)
        {
            if (string.IsNullOrEmpty(location)) return null;
            Match match = Regex.Match(location, "/releases/tag/(?<tag>[^/?#]+)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            return Uri.UnescapeDataString(match.Groups["tag"].Value);
        }

        public static string AssetName(string tag)
        {
            return "ModbusScan-" + tag + ".exe";
        }

        public static string DownloadUrlFor(string tag)
        {
            return "https://github.com/" + Owner + "/" + Repo + "/releases/download/" + tag + "/" + AssetName(tag);
        }

        public static string ReleasePageFor(string tag)
        {
            return "https://github.com/" + Owner + "/" + Repo + "/releases/tag/" + tag;
        }

        private static HttpWebRequest NewRequest(string url, string method)
        {
            return NewRequest(url, method, false);
        }

        private static HttpWebRequest NewRequest(string url, string method, bool followRedirects)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = "ModbusScan/" + CurrentVersion.ToString(3);
            request.AllowAutoRedirect = followRedirects;
            request.Timeout = RequestTimeoutMs;
            request.ReadWriteTimeout = RequestTimeoutMs;
            return request;
        }

        public static Version CurrentVersion
        {
            get { return typeof(Updater).Assembly.GetName().Version; }
        }

        /// <summary>GitHub'daki etiketin kullanılan sürümden yeni olup olmadığını ölçer.</summary>
        public static bool IsNewer(Version current, Version latest)
        {
            if (latest == null) return false;
            if (current == null) return true;
            return latest > current;
        }

        /// <summary>Yeni sürüm olup olmadığını sorgular. Ağ hatasında null döner ve hatayı açıklar.</summary>
        public static UpdateInfo Check(Version current, out string error)
        {
            error = null;
            string tag = null;

            try
            {
                using (WebResponse response = NewRequest(LatestReleaseUrl, "GET").GetResponse())
                {
                    string location = response.Headers["Location"];
                    tag = TagFromLocation(location);
                    if (tag == null)
                    {
                        // Yönlendirme yoksa sayfa zaten en yeni sürüme işaret ediyor olabilir.
                        tag = TagFromLocation(response.ResponseUri.AbsoluteUri);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ReadableError(ex);
                return null;
            }

            if (tag == null)
            {
                error = "Sürüm etiketi yanıtta bulunamadı.";
                return null;
            }

            Version latest = ParseTag(tag);
            if (latest == null)
            {
                error = "Sürüm etiketi çözümlenemedi: " + tag;
                return null;
            }

            if (current == null) current = new Version(0, 0, 0);

            UpdateInfo info = new UpdateInfo();
            info.Tag = tag;
            info.Version = latest;
            info.ReleasePage = ReleasePageFor(tag);
            info.DownloadUrl = DownloadUrlFor(tag);
            info.NewerAvailable = IsNewer(current, latest);

            if (info.NewerAvailable) ProbeAsset(info);
            return info;
        }

        private static void ProbeAsset(UpdateInfo info)
        {
            try
            {
                using (WebResponse response = NewRequest(info.DownloadUrl, "HEAD", true).GetResponse())
                {
                    info.AssetAvailable = true;
                    long length = response.ContentLength;
                    info.SizeBytes = length > 0 ? length : 0;
                }
            }
            catch (Exception ex)
            {
                info.AssetAvailable = false;
                WebException webEx = ex as WebException;
                if (webEx != null && webEx.Response != null) webEx.Response.Close();
            }
        }

        private static string ReadableError(Exception ex)
        {
            WebException webEx = ex as WebException;
            if (webEx != null)
            {
                if (webEx.Status == WebExceptionStatus.Timeout) return "Sunucu yanıt vermedi (8 sn).";
                if (webEx.Status == WebExceptionStatus.NameResolutionFailure) return "github.com çözümlenemedi (ağ yok).";
                if (webEx.Status == WebExceptionStatus.ConnectFailure) return "github.com'a ulaşılamadı (ağ yok).";
                HttpWebResponse http = webEx.Response as HttpWebResponse;
                if (http != null)
                {
                    string message = "HTTP " + (int)http.StatusCode;
                    http.Close();
                    return message;
                }
                return webEx.Message;
            }
            return ex.Message;
        }

        /// <summary>
        /// Yeni exe'yi indirir, doğrular ve kendini değiştirecek betiği başlatır.
        /// Başarılıysa çağıran taraf programı kapatmalıdır.
        /// </summary>
        public static bool TryInstall(UpdateInfo info, string targetExePath, Action<string> status, out string error)
        {
            error = null;
            string downloadPath = Path.Combine(Path.GetTempPath(), AssetName(info.Tag));

            try
            {
                if (!CanWrite(Path.GetDirectoryName(targetExePath)))
                {
                    error = "Programın olduğu klasöre yazılamıyor (yönetici izni gerekir).";
                    return false;
                }

                if (status != null) status("İndiriliyor…");
                long received = DownloadFile(info.DownloadUrl, downloadPath, status);

                if (received < 4096)
                {
                    error = "İndirilen dosya çok küçük (" + received + " bayt).";
                    return false;
                }
                if (!IsExecutable(downloadPath))
                {
                    error = "İndirilen dosya çalıştırılabilir değil (MZ başlığı yok).";
                    return false;
                }
                if (info.SizeBytes > 0 && received != info.SizeBytes)
                {
                    error = "Boyut uyuşmadı: beklenen " + info.SizeBytes + ", gelen " + received + ".";
                    return false;
                }

                if (status != null) status("Güncelleme betiği hazırlanıyor…");
                string scriptPath = WriteReplaceScript(downloadPath, targetExePath, info.ReleasePage);
                StartDetached(scriptPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ReadableError(ex);
                TryDelete(downloadPath);
                return false;
            }
        }

        private static long DownloadFile(string url, string path, Action<string> status)
        {
            HttpWebRequest request = NewRequest(url, "GET", true);
            long total = -1;
            long received = 0;

            using (WebResponse response = request.GetResponse())
            {
                total = response.ContentLength;
                using (Stream source = response.GetResponseStream())
                using (FileStream sink = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[16384];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sink.Write(buffer, 0, read);
                        received += read;
                        if (status != null && total > 0 && received % 65536 < read)
                        {
                            status("İndiriliyor… %" + (received * 100 / total));
                        }
                    }
                }
            }
            return received;
        }

        private static bool IsExecutable(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                int first = fs.ReadByte();
                int second = fs.ReadByte();
                return first == 0x4D && second == 0x5A;
            }
        }

        private static bool CanWrite(string directory)
        {
            try
            {
                string probe = Path.Combine(directory, ".modbusscan-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Çalışan exe kapandıktan sonra kendini yenisiyle değiştirir, programı yeniden başlatır
        /// ve betiğin kendi dosyasını siler. Kilit çözülmezse denemeyi bırakıp sürüm sayfasını açar.
        /// </summary>
        private static string WriteReplaceScript(string downloadPath, string targetExePath, string releasePage)
        {
            StringBuilder bat = new StringBuilder();
            bat.AppendLine("@echo off");
            bat.AppendLine("setlocal");
            bat.AppendLine("set \"NEW=" + downloadPath + "\"");
            bat.AppendLine("set \"OLD=" + targetExePath + "\"");
            bat.AppendLine("set /a TRIES=0");
            bat.AppendLine(":retry");
            bat.AppendLine("set /a TRIES+=1");
            bat.AppendLine("ping -n 2 127.0.0.1 >nul");
            bat.AppendLine("move /y \"%NEW%\" \"%OLD%\" >nul 2>&1");
            bat.AppendLine("if not errorlevel 1 goto done");
            bat.AppendLine("if %TRIES% LSS 20 goto retry");
            bat.AppendLine("start \"\" \"%OLD%\"");
            bat.AppendLine("start \"\" \"" + releasePage + "\"");
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit /b 1");
            bat.AppendLine(":done");
            bat.AppendLine("start \"\" \"%OLD%\"");
            bat.AppendLine("del \"%~f0\"");

            string scriptPath = Path.Combine(Path.GetTempPath(), "ModbusScan-update.cmd");
            File.WriteAllText(scriptPath, bat.ToString(), new UTF8Encoding(false));
            return scriptPath;
        }

        private static void StartDetached(string scriptPath)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = "/c \"" + scriptPath + "\"";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = Path.GetTempPath();
            Process.Start(start);
        }

        public static void OpenUrl(string url)
        {
            Process.Start(url);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "bilinmiyor";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }
}
