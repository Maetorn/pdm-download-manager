using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Markup;

namespace PDM;

/// <summary>
/// UI strings: English, Turkish (in this file), German, French and Italian (LocDe/LocFr/LocIt.cs; a missing key falls
/// back to English). "auto" follows the Windows display language. Language changes take effect after restart.
/// </summary>
public static partial class Loc
{
    /// <summary>Language codes with their own names, in the order the settings list shows them.</summary>
    public static readonly (string Code, string Name)[] Languages =
        [("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("it", "Italiano"), ("tr", "Türkçe")];

    public static string Lang { get; private set; } = "en";

    public static void Init(string lang)
    {
        if (lang is null or "" or "auto") lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Lang = Array.Exists(Languages, l => l.Code == lang) ? lang : "en";
        // Dates and numbers stay in the Windows format; this only steers text (and yt-dlp's subtitle choice).
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Lang);
    }

    public static string T(string key)
    {
        if (!Strings.TryGetValue(key, out var s)) return key;
        return Lang switch
        {
            "en" => s.En,
            "tr" => s.Tr,
            _ => Other(Lang).TryGetValue(key, out var t) ? t : s.En,
        };
    }

    private static Dictionary<string, string> Other(string lang) => lang switch
    {
        "de" => De,
        "fr" => Fr,
        "it" => It,
        _ => Empty,
    };

    private static readonly Dictionary<string, string> Empty = new();

    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    private static readonly Dictionary<string, (string Tr, string En)> Strings = new()
    {
        // toolbar / main
        ["AppTitle"] = ("PDM — İndirme Yöneticisi", "PDM — Download Manager"),
        ["Add"] = ("Ekle", "Add"),
        ["Resume"] = ("Devam", "Resume"),
        ["Pause"] = ("Duraklat", "Pause"),
        ["StopAll"] = ("Tümünü Durdur", "Stop All"),
        ["Delete"] = ("Sil", "Delete"),
        ["DeleteCompleted"] = ("Tamamlananları Temizle", "Clear Completed"),
        ["StartQueue"] = ("Kuyruğu Başlat", "Start Queue"),
        ["StopQueue"] = ("Kuyruğu Durdur", "Stop Queue"),
        ["Scheduler"] = ("Zamanlayıcı", "Scheduler"),
        ["Video"] = ("Video", "Video"),
        ["Settings"] = ("Ayarlar", "Settings"),
        ["Search"] = ("Ara…", "Search…"),
        ["CatAll"] = ("Tüm İndirmeler", "All Downloads"),
        ["CatActive"] = ("İndiriliyor", "Downloading"),
        ["CatUnfinished"] = ("Tamamlanmamış", "Unfinished"),
        ["CatFinished"] = ("Tamamlanan", "Finished"),
        ["CatQueue"] = ("Kuyruk", "Queue"),
        ["CatCompressed"] = ("Sıkıştırılmış", "Compressed"),
        ["CatDocument"] = ("Belgeler", "Documents"),
        ["CatMusic"] = ("Müzik", "Music"),
        ["CatProgram"] = ("Programlar", "Programs"),
        ["CatVideo"] = ("Video", "Video"),
        ["CatImage"] = ("Resimler", "Images"),
        ["CatOther"] = ("Diğer", "Other"),
        ["Categories"] = ("KATEGORİLER", "CATEGORIES"),
        ["ColName"] = ("Dosya adı", "File name"),
        ["ColSize"] = ("Boyut", "Size"),
        ["ColStatus"] = ("Durum", "Status"),
        ["ColProgress"] = ("İlerleme", "Progress"),
        ["ColSpeed"] = ("Hız", "Speed"),
        ["ColEta"] = ("Kalan", "Time left"),
        ["ColAdded"] = ("Eklenme", "Added"),
        ["StatusBarActive"] = ("{0} etkin · {1}", "{0} active · {1}"),
        ["StatusBarLimit"] = ("Hız sınırı: {0}/s", "Speed limit: {0}/s"),
        ["NoLimit"] = ("Hız sınırı yok", "No speed limit"),
        ["BrowserOk"] = ("Tarayıcı bağlantısı hazır",
                         "Browser bridge ready"),
        ["BrowserMissing"] = ("Tarayıcı bağlantısı kurulmadı",
                         "Browser bridge not set up"),
        ["EmptyList"] = ("Henüz indirme yok. Bir bağlantı kopyalayıp Ctrl+V'ye basın ya da tarayıcıda bir dosya indirin.",
                         "No downloads yet. Copy a link and press Ctrl+V, or download a file in your browser."),

        // statuses
        ["StQueued"] = ("Sırada", "Queued"),
        ["StConnecting"] = ("Bağlanıyor…", "Connecting…"),
        ["StDownloading"] = ("İndiriliyor", "Downloading"),
        ["StPaused"] = ("Duraklatıldı", "Paused"),
        ["StCompleted"] = ("Tamamlandı", "Completed"),
        ["StError"] = ("Hata", "Error"),
        ["StProcessing"] = ("Taranıyor…", "Scanning…"),
        ["StRetry"] = ("Yeniden deneniyor…", "Retrying…"),
        ["StMerge"] = ("Birleştiriliyor…", "Merging…"),
        ["StConvert"] = ("Dönüştürülüyor…", "Converting…"),
        ["StChanged"] = ("Sunucudaki dosya değişmiş, baştan", "File changed on server, restarting"),

        // errors
        ["ErrHttp"] = ("Sunucu hatası {0}", "Server error {0}"),
        ["ErrHttp403"] = ("Erişim reddedildi (403). Bağlantının süresi dolmuş olabilir — Adresi Yenile'yi deneyin.", "Access denied (403). The link may have expired — try Refresh Address."),
        ["ErrHttp404"] = ("Dosya bulunamadı (404)", "File not found (404)"),
        ["ErrHttp410"] = ("Bağlantının süresi dolmuş (410) — Adresi Yenile'yi deneyin.", "Link expired (410) — try Refresh Address."),
        ["ErrDiskFull"] = ("Disk dolu", "Disk is full"),
        ["ErrChanged"] = ("Sunucudaki dosya değişti; ilerleme sıfırlandı", "File changed on the server; progress was reset"),
        ["ErrNoRange"] = ("Sunucu parçalı indirmeyi bıraktı; baştan indirilecek", "Server stopped supporting ranges; will restart"),
        ["ErrNetwork"] = ("Bağlantı hatası", "Connection error"),
        ["ErrFs"] = ("Dosya yazılamadı", "Could not write the file"),
        ["ErrBlocked"] = ("Antivirüs veya Windows ilkesi dosyayı engelledi", "Blocked by antivirus or Windows policy"),
        ["ErrHash"] = ("SHA-256 eşleşmedi! Dosya beklenen dosya değil.", "SHA-256 mismatch! The file is not the expected one."),
        ["ErrUnknown"] = ("Hata: {0}", "Error: {0}"),
        ["ErrYtDlpMissing"] = ("Video araçları kurulu değil (Ayarlar → Video)", "Video tools are not installed (Settings → Video)"),
        ["ErrDrm"] = ("DRM korumalı içerik indirilemez", "DRM-protected content cannot be downloaded"),

        // context menu
        ["Open"] = ("Aç", "Open"),
        ["OpenFolder"] = ("Klasörü aç", "Open folder"),
        ["Restart"] = ("Baştan indir", "Restart"),
        ["RefreshUrl"] = ("Adresi yenile…", "Refresh address…"),
        ["CopyUrl"] = ("Adresi kopyala", "Copy address"),
        ["Properties"] = ("Özellikler", "Properties"),
        ["AddToQueue"] = ("Kuyruğa ekle", "Add to queue"),
        ["RemoveFromQueue"] = ("Kuyruktan çıkar", "Remove from queue"),
        ["MoveUp"] = ("Kuyrukta yukarı", "Move up"),

        // progress / complete windows
        ["FileSize"] = ("Dosya boyutu", "File size"),
        ["ResumeCapability"] = ("Devam ettirme desteği", "Resume capability"),
        ["ActiveConnections"] = ("{0} bağlantı", "{0} connections"),
        ["Retry"] = ("Yeniden dene", "Retry"),
        ["DownloadedBytes"] = ("İndirilen: {0} ({1} bayt)", "Downloaded: {0} ({1} bytes)"),
        ["SavedAs"] = ("Kaydedildiği yer", "Saved as"),
        ["OpenWith"] = ("Birlikte aç…", "Open with…"),
        ["DontShowAgain"] = ("Bu pencereyi bir daha gösterme", "Don't show this window again"),
        ["ShowProgressWindow"] = ("İndirme başlayınca ilerleme penceresini göster", "Show the progress window when a download starts"),
        ["ShowCompleteDialog"] = ("İndirme bitince \"İndirme tamamlandı\" penceresini göster", "Show the \"Download complete\" window when a download finishes"),
        ["SpeedLimitToggle"] = ("Hız Sınırı", "Speed Limit"),
        ["ItemSpeedLimit"] = ("Hız sınırı…", "Speed limit…"),
        ["LimitThisDownload"] = ("Bu indirmeyi sınırla", "Limit this download"),
        ["ItemSpeedLimitPrompt"] = ("Bu indirme için hız sınırı (KB/s, 0 = sınırsız). Genel sınır da geçerlidir.", "Speed limit for this download (KB/s, 0 = none). The global limit still applies."),
        ["AutoRetryAt"] = ("{0}'da yeniden denenecek", "retrying at {0}"),
        ["PreventSleep"] = ("İndirme sürerken bilgisayarın uykuya geçmesini engelle", "Keep the computer awake while downloading"),
        ["AutoRetry"] = ("Bağlantı kopunca veya sunucu meşgulken indirmeleri kendiliğinden yeniden dene", "Retry downloads automatically when the connection drops or the server is busy"),
        ["NoSpace"] = ("Diskte yeterli yer yok: {0} boş, {1} gerekli.", "Not enough disk space: {0} free, {1} needed."),
        ["PatternFound"] = ("Toplu desen: {0} dosya. \"İndir\" ile listesi açılır.", "Batch pattern: {0} files. \"Download\" opens the list."),
        ["AlreadyInList"] = ("[listede var: {0}]", "[already in the list: {0}]"),
        ["DuplicateText"] = ("Bu adres listede zaten var:\n\n{0}  ({1})\n\nNe yapılsın?", "This address is already in the list:\n\n{0}  ({1})\n\nWhat should happen?"),
        ["DuplicateShow"] = ("Listede göster", "Show in the list"),
        ["DuplicateAgain"] = ("Yine de indir", "Download again"),
        ["HideDetails"] = ("Ayrıntıları gizle", "Hide details"),
        ["ShowDetails"] = ("Ayrıntıları göster", "Show details"),
        ["SegmentsTitle"] = ("Bağlantıların başlangıç noktaları ve ilerlemesi", "Start positions and progress by connection"),
        ["ConnNo"] = ("No.", "No."),
        ["ConnInfo"] = ("Bilgi", "Info"),
        ["ConnReceiving"] = ("Veri alınıyor…", "Receiving data…"),

        // add dialog
        ["AddTitle"] = ("Yeni İndirme", "New Download"),
        ["BrowserDownloadTitle"] = ("İndirme Bilgisi", "Download Info"),
        ["Url"] = ("Adres (URL)", "Address (URL)"),
        ["FileName"] = ("Dosya adı", "File name"),
        ["SaveTo"] = ("Kaydet", "Save to"),
        ["Browse"] = ("Gözat…", "Browse…"),
        ["Connections"] = ("Bağlantı", "Connections"),
        ["Advanced"] = ("Gelişmiş", "Advanced"),
        ["ExpectedSha"] = ("Beklenen SHA-256 (isteğe bağlı)", "Expected SHA-256 (optional)"),
        ["Referrer"] = ("Yönlendiren sayfa (Referer)", "Referrer"),
        ["DownloadNow"] = ("İndir", "Download"),
        ["DownloadLater"] = ("Sonra İndir", "Download Later"),
        ["Cancel"] = ("İptal", "Cancel"),
        ["Close"] = ("Kapat", "Close"),
        ["Ok"] = ("Tamam", "OK"),
        ["Save"] = ("Kaydet", "Save"),
        ["Checking"] = ("Bilgi alınıyor…", "Checking…"),
        ["SizeUnknown"] = ("Boyut bilinmiyor", "Size unknown"),
        ["Resumable"] = ("Devam ettirilebilir", "Resumable"),
        ["NotResumable"] = ("Devam ettirilemez", "Not resumable"),
        ["ProbeFailed"] = ("Bilgi alınamadı: {0}", "Could not check: {0}"),
        ["InvalidUrl"] = ("Geçerli bir http/https adresi girin.", "Enter a valid http/https address."),
        ["BatchLinks"] = ("{0} bağlantı", "{0} links"),
        ["FileExists"] = ("Bu adda bir dosya var; \"{0}\" olarak kaydedilecek.", "A file with this name exists; it will be saved as \"{0}\"."),
        ["ExeWarning"] = ("Bu bir program dosyası. Yalnızca güvendiğiniz kaynaklardan indirin.", "This is a program file. Only download it from sources you trust."),
        ["SelectAll"] = ("Tümünü seç", "Select all"),
        ["SelectNone"] = ("Hiçbiri", "None"),
        ["Filter"] = ("Filtre (ör. .zip .pdf)", "Filter (e.g. .zip .pdf)"),

        // delete
        ["DeleteTitle"] = ("İndirmeleri sil", "Delete downloads"),
        ["DeleteConfirm"] = ("{0} indirme listeden kaldırılsın mı?", "Remove {0} download(s) from the list?"),
        ["DeleteFilesToo"] = ("Tamamlanmış dosyaları da Geri Dönüşüm Kutusu'na taşı", "Also move finished files to the Recycle Bin"),

        // details
        ["DetailsTitle"] = ("İndirme Ayrıntıları", "Download Details"),
        ["Downloaded"] = ("İndirilen", "Downloaded"),
        ["Speed"] = ("Hız", "Speed"),
        ["TimeLeft"] = ("Kalan süre", "Time left"),
        ["Status"] = ("Durum", "Status"),
        ["Path"] = ("Konum", "Location"),
        ["Segments"] = ("Parçalar", "Segments"),
        ["ComputeSha"] = ("SHA-256 hesapla", "Compute SHA-256"),
        ["Verify"] = ("Doğrula", "Verify"),
        ["HashMatch"] = ("✓ Eşleşti", "✓ Matches"),
        ["HashNoMatch"] = ("✗ EŞLEŞMEDİ", "✗ DOES NOT MATCH"),
        ["RefreshPrompt"] = ("Yeni indirme adresini yapıştırın. İlerleme korunur; dosya boyutu aynı olmalı.", "Paste the new download address. Progress is kept; the file size must be the same."),
        ["Yes"] = ("Evet", "Yes"),
        ["No"] = ("Hayır", "No"),

        // open warnings
        ["OpenExeTitle"] = ("Program dosyası", "Program file"),
        ["OpenExeText"] = ("\"{0}\" bir program dosyası ve internetten indirildi. Çalıştırmak istediğinize emin misiniz?\n\nKaynak: {1}",
                           "\"{0}\" is a program downloaded from the internet. Are you sure you want to run it?\n\nSource: {1}"),

        // video
        ["VideoTitle"] = ("Video İndir", "Download Video"),
        ["Page"] = ("Sayfa", "Page"),
        ["FoundMedia"] = ("Sayfada bulunan medya dosyaları", "Media files found on the page"),
        ["FoundMediaCount"] = ("Sayfada bulunan diğer medya dosyaları ({0})", "Other media files found on the page ({0})"),
        ["NoMediaFound"] = ("Doğrudan medya bağlantısı bulunamadı.", "No direct media links were found."),
        ["DownloadSelected"] = ("Seçileni indir", "Download selected"),
        ["Quality"] = ("Kalite", "Quality"),
        ["AudioOnly"] = ("Sadece ses", "Audio only"),
        ["Container"] = ("Biçim", "Format"),
        ["Subtitles"] = ("Altyazıları da indir (uygulama dili ve İngilizce)",
                     "Also download subtitles (app language and English)"),
        ["WholePlaylist"] = ("Tüm oynatma listesini indir", "Download the whole playlist"),
        ["GetQualities"] = ("Kaliteleri getir", "Get qualities"),
        ["Analyzing"] = ("Kaliteler alınıyor…", "Getting qualities…"),
        ["Best"] = ("En iyi", "Best"),
        ["ToolsMissing"] = ("YouTube ve benzeri siteler için yt-dlp + FFmpeg (+ YouTube için Deno) gerekir. İsterseniz resmi GitHub sürümlerinden indirilip SHA-256 ile doğrulanarak kurulur.",
                            "YouTube and similar sites need yt-dlp + FFmpeg (+ Deno for YouTube). If you want, they are downloaded from the official GitHub releases and verified with SHA-256."),
        ["InstallTools"] = ("Video araçlarını kur", "Install video tools"),
        ["Installing"] = ("Kuruluyor… {0:0}%", "Installing… {0:0}%"),
        ["LegalNote"] = ("Yalnızca indirme hakkınız olan içerikler içindir. DRM korumalı içerik desteklenmez.",
                         "Only for content you are allowed to download. DRM-protected content is not supported."),
        ["Playlist"] = ("Oynatma listesi · {0} video", "Playlist · {0} videos"),

        // scheduler
        ["SchedTitle"] = ("Kuyruklar ve Zamanlayıcı", "Queues and Scheduler"),
        ["SchedEnable"] = ("Belirtilen saatte başlat", "Start at a set time"),
        ["SchedDays"] = ("Günler", "Days"),
        ["DayNames"] = ("Pzt,Sal,Çar,Per,Cum,Cmt,Paz", "Mon,Tue,Wed,Thu,Fri,Sat,Sun"),
        ["Queues"] = ("Kuyruklar", "Queues"),
        ["MainQueue"] = ("Ana kuyruk", "Main queue"),
        ["QueueNew"] = ("Yeni kuyruk…", "New queue…"),
        ["QueueRename"] = ("Yeniden adlandır…", "Rename…"),
        ["QueueNamePrompt"] = ("Kuyruğun adı:", "Queue name:"),
        ["QueueDefaultName"] = ("Kuyruk {0}", "Queue {0}"),
        ["QueueDeleteConfirm"] = ("\"{0}\" kuyruğu silinsin mi? İçindeki indirmeler ana kuyruğa geçer.", "Delete the queue \"{0}\"? Its downloads move to the main queue."),
        ["QueueParallel"] = ("Aynı anda indirilecek", "Files at the same time"),
        ["QueueParallelDefault"] = ("Genel ayar", "General setting"),
        ["QueueForLater"] = ("Sonra İndir kuyruğu", "Queue for \"Later\""),
        ["SchedStart"] = ("Başlangıç", "Start at"),
        ["SchedStopEnable"] = ("Belirtilen zamanda durdur", "Stop at a set time"),
        ["SchedStop"] = ("Bitiş", "Stop at"),
        ["SchedDaily"] = ("Saatler her gün tekrarlanır.", "The times repeat every day."),
        ["SchedAfter"] = ("Kuyruk bitince", "When the queue finishes"),
        ["PostNone"] = ("Hiçbir şey yapma", "Do nothing"),
        ["PostExit"] = ("PDM'yi kapat", "Exit PDM"),
        ["PostSleep"] = ("Bilgisayarı uyku moduna al", "Put the computer to sleep"),
        ["PostShutdown"] = ("Bilgisayarı kapat", "Shut down the computer"),
        ["SchedHint"] = ("Kuyrukta: \"Sonra İndir\" ile ya da sağ tık → \"Kuyruğa ekle\" ile eklenen indirmeler. Saatler seçilen günlerde tekrarlanır.", "In a queue: downloads added with \"Download Later\" or right click → \"Add to queue\". The times repeat on the chosen days."),
        ["More"] = ("Diğer", "More"),
        ["Extract"] = ("Arşivi çıkar", "Extract archive"),
        ["Extracted"] = ("Arşiv çıkarıldı", "Archive extracted"),
        ["ExtractFailed"] = ("Arşiv çıkarılamadı", "Could not extract"),
        ["ExtractNoTar"] = ("7z/rar için Windows 10 (1803) veya daha yenisi gerekir.", "7z/rar needs Windows 10 (1803) or newer."),
        ["ExtractBad"] = ("Arşiv bozuk, şifreli veya desteklenmiyor.", "The archive is damaged, encrypted or not supported."),
        ["AutoExtract"] = ("İnen arşivleri otomatik çıkar (zip, 7z, rar, tar)", "Extract downloaded archives automatically (zip, 7z, rar, tar)"),
        ["DeleteArchive"] = ("Çıkardıktan sonra arşivi Geri Dönüşüm Kutusu'na taşı", "Move the archive to the Recycle Bin after extracting"),
        ["AutoExtractHint"] = ("Arşiv, yanındaki aynı adlı yeni bir klasöre çıkarılır. Çıkan dosyalar asla çalıştırılmaz ve \"internetten indirildi\" işaretini taşır.", "The archive is extracted into a new folder with the same name next to it. Extracted files are never run and keep the \"downloaded from the internet\" mark."),
        ["HistoryStats"] = ("Geçmiş ve istatistikler", "History and statistics"),
        ["ExportList"] = ("Listeyi dışa aktar…", "Export list…"),
        ["ImportList"] = ("Listeyi içe aktar…", "Import list…"),
        ["ExportEmpty"] = ("Dışa aktarılacak indirme yok.", "Nothing to export."),
        ["ExportFilter"] = ("Bağlantı listesi (*.txt)|*.txt|PDM listesi (*.json)|*.json", "Link list (*.txt)|*.txt|PDM list (*.json)|*.json"),
        ["Exported"] = ("{0} indirme dışa aktarıldı.", "{0} downloads exported."),
        ["ImportFilter"] = ("Bağlantı listeleri (*.txt;*.json)|*.txt;*.json|Tüm dosyalar (*.*)|*.*", "Link lists (*.txt;*.json)|*.txt;*.json|All files (*.*)|*.*"),
        ["ImportTooBig"] = ("Dosya çok büyük (en fazla 20 MB).", "The file is too large (20 MB at most)."),
        ["ImportNone"] = ("Dosyada indirme bağlantısı bulunamadı.", "No download links were found in the file."),
        ["HistoryDate"] = ("Bitiş", "Finished"),
        ["DownloadAgain"] = ("Yeniden indir", "Download again"),
        ["HistoryClear"] = ("Geçmişi temizle", "Clear history"),
        ["StatsReset"] = ("İstatistikleri sıfırla", "Reset statistics"),
        ["HistoryClearConfirm"] = ("Geçmiş listesi silinsin mi? (Dosyalar silinmez, istatistikler kalır.)", "Clear the history list? (Files are not deleted, statistics stay.)"),
        ["StatsResetConfirm"] = ("İstatistikler sıfırlansın mı?", "Reset the statistics?"),
        ["StatsTotal"] = ("Toplam", "Total"),
        ["StatsToday"] = ("Bugün", "Today"),
        ["Stats7"] = ("Son 7 gün", "Last 7 days"),
        ["Stats30"] = ("Son 30 gün", "Last 30 days"),
        ["Stats30Chart"] = ("Son 30 gün, günlük", "Last 30 days, per day"),
        ["FilesCount"] = ("{0} dosya", "{0} files"),
        ["CheckLinks"] = ("Bağlantıları denetle", "Check links"),
        ["CheckLinksTip"] = ("Her bağlantının boyutuna ve açılıp açılmadığına bakar; açılmayanların işaretini kaldırır.", "Checks each link's size and whether it works; unticks the ones that fail."),
        ["CheckingLinks"] = ("Denetleniyor: {0}/{1}…", "Checking: {0}/{1}…"),
        ["LinksChecked"] = ("Çalışan: {0} · Hatalı: {1} · Toplam boyut: {2}", "Working: {0} · Failed: {1} · Total size: {2}"),
        ["ProbeTimeout"] = ("zaman aşımı", "timeout"),
        ["Unreachable"] = ("ulaşılamadı", "unreachable"),
        ["CategoryFolderHint"] = ("Her dosya türüne göre alt klasöre kaydedilir (Video, Music, Compressed, Documents, Programs).", "Each file goes to the subfolder of its type (Video, Music, Compressed, Documents, Programs)."),
        ["ErrHttp401"] = ("Site kullanıcı adı ve şifre istiyor (Ayarlar → Site girişleri).", "The site asks for a username and password (Settings → Site logins)."),
        ["ErrHttp407"] = ("Proxy kullanıcı adı ve şifre istiyor (Ayarlar → Bağlantı → Proxy).", "The proxy asks for a username and password (Settings → Connection → Proxy)."),
        ["SetLogins"] = ("Site girişleri", "Site logins"),
        ["LoginsIntro"] = ("Kullanıcı adı ve şifre isteyen siteler (HTTP Basic/Digest girişi). PDM şifreyi yalnızca o siteye, o site isterse gönderir.", "Sites that ask for a username and password (HTTP Basic/Digest). PDM sends the password only to that site, and only when it asks."),
        ["LoginAdd"] = ("Giriş ekle…", "Add login…"),
        ["LoginEdit"] = ("Düzenle…", "Edit…"),
        ["LoginHost"] = ("Site", "Site"),
        ["LoginUser"] = ("Kullanıcı adı", "Username"),
        ["LoginPassword"] = ("Şifre", "Password"),
        ["PasswordKept"] = ("Kayıtlı şifre korunur; değiştirmek için yenisini yazın.", "The saved password is kept; type a new one to change it."),
        ["LoginHint"] = ("\"ornek.com\" alt alan adlarını da kapsar. Şifre Windows DPAPI ile şifreli saklanır. Şifreyi açık gönderen \"Basic\" giriş yalnızca https ile kullanılır.", "\"example.com\" also covers its subdomains. The password is stored encrypted with Windows DPAPI. \"Basic\" logins, which send the password readable, are used over https only."),
        ["LoginNeeded"] = ("Bu site kullanıcı adı ve şifre istiyor.", "This site asks for a username and password."),
        ["Proxy"] = ("Proxy", "Proxy"),
        ["ProxyMode"] = ("Bağlantı", "Connection"),
        ["ProxySystem"] = ("Windows proxy ayarı", "Windows proxy setting"),
        ["ProxyNone"] = ("Proxy kullanma (doğrudan)", "No proxy (direct)"),
        ["ProxyCustom"] = ("Bu proxy'yi kullan", "Use this proxy"),
        ["ProxyAddress"] = ("Adres", "Address"),
        ["ProxyHint"] = ("Örnek: 127.0.0.1:8080 (HTTP) veya socks5://127.0.0.1:1080. Tüm indirmeler, video araçları ve güncellemeler bu yoldan gider.", "Example: 127.0.0.1:8080 (HTTP) or socks5://127.0.0.1:1080. All downloads, the video tools and updates go this way."),
        ["Grabber"] = ("Site tarayıcı…", "Site grabber…"),
        ["GrabStart"] = ("Başlangıç sayfası", "Start page"),
        ["GrabDepth"] = ("Derinlik", "Depth"),
        ["GrabDepth0"] = ("Yalnızca bu sayfa", "This page only"),
        ["GrabDepth1"] = ("Bu sayfa + bağlantılı sayfalar", "This page + linked pages"),
        ["GrabDepth2"] = ("2 adım", "2 steps"),
        ["GrabDepth3"] = ("3 adım", "3 steps"),
        ["GrabSameSite"] = ("Yalnızca aynı sitedeki sayfaları gez (dosyalar başka sitede olabilir)", "Follow pages on the same site only (files may be elsewhere)"),
        ["GrabTypes"] = ("Dosya türleri", "File types"),
        ["GrabTypesHint"] = ("Örnek: zip pdf mp3 jpg. Boş bırakılırsa sayfa olmayan tüm bağlantılar (css/js hariç).", "Example: zip pdf mp3 jpg. Empty = every link that is not a page (except css/js)."),
        ["GrabMaxPages"] = ("En fazla sayfa", "Max pages"),
        ["GrabRun"] = ("Tara", "Scan"),
        ["GrabStop"] = ("Durdur", "Stop"),
        ["GrabProgress"] = ("{0} sayfa okundu, {1} dosya bulundu…", "{0} pages read, {1} files found…"),
        ["GrabNone"] = ("Uygun dosya bulunamadı.", "No matching files were found."),
        ["GrabStopped"] = ("Durduruldu.", "Stopped."),
        ["Convert"] = ("Dönüştür", "Convert"),
        ["ConvertMp3"] = ("MP3'e dönüştür (ses)", "Convert to MP3 (audio)"),
        ["ConvertMp4"] = ("MP4'e dönüştür (H.264, her yerde açılır)", "Convert to MP4 (H.264, plays everywhere)"),
        ["Converting"] = ("Dönüştürülüyor…", "Converting…"),
        ["ConvertingPct"] = ("Dönüştürülüyor… %{0:0}", "Converting… {0:0}%"),
        ["ConvertFailed"] = ("Dönüştürülemedi", "Could not convert"),
        ["ConvertNoAudio"] = ("dosyada ses yok", "the file has no sound"),
        ["ConvertNoVideo"] = ("dosyada görüntü yok", "the file has no picture"),
        ["ConvertMissing"] = ("dosya bulunamadı", "file not found"),
        ["Install"] = ("Kur", "Install"),
        ["FfmpegNeededTitle"] = ("FFmpeg gerekli", "FFmpeg is needed"),
        ["FfmpegNeeded"] = ("Dönüştürmek için video araçları (yt-dlp + FFmpeg, resmi GitHub sürümleri, SHA-256 ile doğrulanır) bir kez kurulmalı.", "Converting needs the video tools (yt-dlp + FFmpeg, official GitHub releases, verified with SHA-256), installed once."),
        ["ProxyBad"] = ("Proxy adresi geçersiz. Örnek: 127.0.0.1:8080 veya socks5://127.0.0.1:1080", "The proxy address is not valid. Example: 127.0.0.1:8080 or socks5://127.0.0.1:1080"),
        ["ShutdownCountdown"] = ("Kuyruk tamamlandı. {0} saniye içinde: {1}", "Queue finished. In {0} seconds: {1}"),

        // settings
        ["SetGeneral"] = ("Genel", "General"),
        ["SetFiles"] = ("Dosyalar", "Files"),
        ["SetConnection"] = ("Bağlantı", "Connection"),
        ["SetBrowser"] = ("Tarayıcı", "Browser"),
        ["SetSecurity"] = ("Güvenlik", "Security"),
        ["SetVideo"] = ("Video", "Video"),
        ["SetAbout"] = ("Hakkında", "About"),
        ["SetUpdate"] = ("Güncelleme", "Updates"),
        ["UpdVersion"] = ("Yüklü sürüm", "Installed version"),
        ["UpdAuto"] = ("Güncellemeleri otomatik kur (indirme yokken; PDM kendini yeniden başlatır)", "Install updates automatically (when nothing is downloading; PDM restarts itself)"),
        ["UpdBusy"] = ("GitHub şu an çok fazla istek alıyor; PDM daha sonra yeniden dener.", "GitHub is limiting requests right now; PDM will try again later."),
        ["LangAuto"] = ("Otomatik (Windows dili)", "Automatic (Windows language)"),
        ["UpdCheckNow"] = ("Şimdi denetle", "Check now"),
        ["UpdChecking"] = ("Denetleniyor…", "Checking…"),
        ["UpdUpToDate"] = ("PDM güncel (sürüm {0}).", "PDM is up to date (version {0})."),
        ["UpdDownloading"] = ("Sürüm {0} indiriliyor… {1:0}%", "Downloading version {0}… {1:0}%"),
        ["UpdReady"] = ("Sürüm {0} indirildi ve doğrulandı.", "Version {0} downloaded and verified."),
        ["UpdAskRestart"] = ("PDM {0} hazır. Şimdi yeniden başlatılıp güncellensin mi?\n\nSüren indirmeler duraklatılır ve yeniden açılınca kaldığı yerden devam eder.", "PDM {0} is ready. Restart and update now?\n\nRunning downloads are paused and continue after the restart."),
        ["UpdDone"] = ("PDM {0} sürümüne güncellendi.", "PDM was updated to version {0}."),
        ["UpdFailed"] = ("Güncelleme başarısız: {0}", "Update failed: {0}"),
        ["UpdHelp"] = ("Güncellemeler yalnızca projenin GitHub sürümlerinden (github.com/Maetorn/pdm-download-manager/releases) indirilir ve yanında yayınlanan SHA-256 ile doğrulanır; tutmazsa kurulmaz. Hesap veya anahtar gerekmez.",
                     "Updates come only from the project's GitHub releases (github.com/Maetorn/pdm-download-manager/releases) and are checked against the SHA-256 published with them; a file that does not match is never installed. No account or token is needed."),
        ["Language"] = ("Dil (yeniden başlatınca)", "Language (after restart)"),
        ["Theme"] = ("Tema (yeniden başlatınca)", "Theme (after restart)"),
        ["ThemeSystem"] = ("Sistem", "System"),
        ["ThemeLight"] = ("Açık", "Light"),
        ["ThemeDark"] = ("Koyu", "Dark"),
        ["StartWithWindows"] = ("Windows açılınca başlat (tepside)", "Start with Windows (in tray)"),
        ["MinimizeToTray"] = ("Kapatınca sistem tepsisinde çalışmaya devam et", "Keep running in the tray when closed"),
        ["Notify"] = ("İndirme bitince bildirim göster", "Show a notification when a download finishes"),
        ["ClipboardMonitor"] = ("Panoya kopyalanan indirme bağlantılarını yakala", "Catch download links copied to the clipboard"),
        ["DefaultFolder"] = ("Varsayılan indirme klasörü", "Default download folder"),
        ["CategoryFolders"] = ("Türe göre alt klasörlere kaydet (Video, Müzik, …)", "Save into subfolders by type (Video, Music, …)"),
        ["AutoRename"] = ("Aynı adda dosya varsa otomatik yeniden adlandır", "Rename automatically if the file exists"),
        ["DefaultConnections"] = ("Varsayılan bağlantı sayısı", "Default connections per download"),
        ["MaxConcurrent"] = ("Aynı anda en fazla indirme", "Max simultaneous downloads"),
        ["SpeedLimit"] = ("Hız sınırı (KB/s, 0 = sınırsız)", "Speed limit (KB/s, 0 = unlimited)"),
        ["Retries"] = ("Yeniden deneme sayısı", "Retry count"),
        ["Timeout"] = ("Zaman aşımı (sn)", "Timeout (s)"),
        ["UserAgent"] = ("Özel User-Agent (boş = tarayıcınınki)", "Custom User-Agent (empty = your browser's)"),
        ["BrowserStatus"] = ("Tarayıcı köprüsü",
                         "Browser bridge"),
        ["Registered"] = ("Kayıtlı ✓", "Registered ✓"),
        ["NotRegistered"] = ("Kayıtlı değil", "Not registered"),
        ["Reregister"] = ("Yeniden kaydet", "Register again"),
        ["OpenExtFolder"] = ("Eklenti klasörünü aç", "Open extension folder"),
        ["HowToInstallExt"] = ("Tarayıcı eklentisi nasıl kurulur?",
                         "How to install the browser extension?"),
        ["ShowDialogBrowser"] = ("Tarayıcıdan gelen indirmelerde bilgi penceresini göster", "Show the info window for downloads from the browser"),
        ["AcceptCookies"] = ("Tarayıcıdan gelen çerezleri kullan (oturum gerektiren indirmeler için; şifreli saklanır, bitince silinir)",
                             "Use cookies sent by the browser (for downloads that need a login; stored encrypted, deleted when done)"),
        ["ScanFiles"] = ("Biten dosyaları Windows'a teslim et (\"internetten indirildi\" işareti + antivirüs taraması)",
                         "Hand finished files to Windows (\"downloaded from the internet\" mark + antivirus scan)"),
        ["WarnExe"] = ("Program dosyalarını açmadan önce uyar", "Warn before opening program files"),
        ["SecurityInfo"] = ("• PDM hiçbir ağ portu açmaz. Tarayıcı eklentisi yalnızca bu kullanıcıya açık yerel bir kanaldan (named pipe) bağlanır; yalnızca PDM eklentisinin kimliği kabul edilir.\n• Yalnızca http/https bağlantıları indirilir; sertifika doğrulaması kapatılamaz.\n• Dosya adları temizlenir (klasör dışına yazma, gizli Unicode hileleri, ayrılmış adlar engellenir).\n• İndirilen dosyalar asla otomatik çalıştırılmaz.\n• Çerezler Windows DPAPI ile şifrelenir ve indirme bitince silinir; günlüklere adres sorguları, çerezler ve başlıklar yazılmaz.",
                             "• PDM opens no network ports. The browser extension connects through a local channel (named pipe) open only to this user; only the PDM extension ID is accepted.\n• Only http/https links are downloaded; certificate checks cannot be turned off.\n• File names are cleaned (writing outside the folder, hidden Unicode tricks and reserved names are blocked).\n• Downloaded files are never run automatically.\n• Cookies are encrypted with Windows DPAPI and deleted when the download finishes; logs never contain query strings, cookies or headers."),
        ["VideoTools"] = ("Video araçları", "Video tools"),
        ["Installed"] = ("Kurulu", "Installed"),
        ["NotInstalled"] = ("Kurulu değil", "Not installed"),
        ["UpdateTools"] = ("Güncelle", "Update"),
        ["RemoveTools"] = ("Kaldır", "Remove"),
        ["PreferredQuality"] = ("Tercih edilen kalite", "Preferred quality"),
        ["DataFolder"] = ("Veri klasörü", "Data folder"),
        ["Logs"] = ("Günlükler", "Logs"),
        ["Uninstall"] = ("PDM'yi kaldır…", "Uninstall PDM…"),
        ["AboutText"] = ("PDM — indirme yöneticisi\nSürüm {0}\n\nAçık kaynak, MIT lisansı: github.com/Maetorn/pdm-download-manager\nİndirme motoru, arayüz ve tarayıcı eklentisi bu proje için yazıldı; üçüncü taraf kütüphane kullanılmaz.\nİsteğe bağlı video araçları ayrı programlardır: yt-dlp (Unlicense), FFmpeg (GPL), Deno (MIT). Yalnızca siz kurarsanız indirilir.",
                     "PDM — download manager\nVersion {0}\n\nOpen source, MIT license: github.com/Maetorn/pdm-download-manager\nThe download engine, UI and browser extension were written for this project; no third-party libraries are used.\nThe optional video tools are separate programs: yt-dlp (Unlicense), FFmpeg (GPL), Deno (MIT). They are only downloaded if you install them."),

        // tray / notifications
        ["TrayOpen"] = ("PDM'yi aç", "Open PDM"),
        ["TrayAdd"] = ("Bağlantı ekle…", "Add link…"),
        ["TrayPauseAll"] = ("Tümünü duraklat", "Pause all"),
        ["TrayExit"] = ("Çıkış", "Exit"),
        ["NotifyDone"] = ("İndirme tamamlandı", "Download complete"),
        ["NotifyFailed"] = ("İndirme başarısız", "Download failed"),
        ["StillRunning"] = ("PDM tepside çalışmaya devam ediyor.", "PDM keeps running in the tray."),
        ["ClipboardFound"] = ("Panoda indirme bağlantısı bulundu — eklemek için tıklayın", "Download link found on the clipboard — click to add"),
        ["ExitWhileActive"] = ("{0} indirme sürüyor. Çıkılsın mı? (Kaldıkları yerden devam edecekler.)", "{0} downloads are running. Exit? (They will resume later.)"),

        // setup
        ["SetupTitle"] = ("PDM Kurulumu", "PDM Setup"),
        ["SetupIntro"] = ("PDM bu bilgisayarda yalnızca sizin kullanıcınız için kurulacak (yönetici izni gerekmez).", "PDM will be installed for your user only (no administrator rights needed)."),
        ["SetupLocation"] = ("Kurulum yeri", "Install location"),
        ["SetupDesktop"] = ("Masaüstü kısayolu oluştur", "Create a desktop shortcut"),
        ["SetupOpera"] = ("Tarayıcı entegrasyonunu kur (Opera, Chrome, Edge)",
                         "Set up browser integration (Opera, Chrome, Edge)"),
        ["SetupInstall"] = ("Kur", "Install"),
        ["SetupDone"] = ("Kurulum tamamlandı.", "Setup complete."),
        ["SetupLaunch"] = ("PDM'yi başlat", "Launch PDM"),
        ["SetupFailed"] = ("Kurulum başarısız: {0}", "Setup failed: {0}"),
        ["UninstallConfirm"] = ("PDM kaldırılsın mı? İndirdiğiniz dosyalar silinmez.", "Uninstall PDM? Your downloaded files are not deleted."),
        ["UninstallData"] = ("Ayarlar, indirme listesi ve video araçları da silinsin mi?", "Also delete settings, the download list and video tools?"),
        ["UninstallDone"] = ("PDM kaldırıldı. Tarayıcı eklentisini eklentiler sayfasından (opera://extensions, chrome://extensions, edge://extensions) kaldırabilirsiniz.",
                         "PDM was removed. You can remove the browser extension on the extensions page (opera://extensions, chrome://extensions, edge://extensions)."),

        // extension guide
        ["ExtGuideTitle"] = ("Tarayıcı eklentisini kurma",
                         "Installing the browser extension"),
        ["ExtGuide"] = ("Opera, Chrome ve Edge için aynı eklenti kullanılır; birden fazla tarayıcıya aynı klasörden yükleyebilirsiniz.\n\n1. Aşağıdan tarayıcınızın düğmesine basın (eklentiler sayfası açılır; açılmazsa adres panoya kopyalanır, adres çubuğuna yapıştırın):\n    Opera: opera://extensions   ·   Chrome: chrome://extensions   ·   Edge: edge://extensions\n2. \"Geliştirici modu\"nu açın (Opera ve Chrome: sağ üstte; Edge: sol tarafta).\n3. \"Paketlenmemiş öğe yükle\" (Load unpacked) düğmesine basın.\n4. Şu klasörü seçin:\n    {0}\n5. PDM eklentisi görünür. Kimliği şu olmalı: {1}\n\nBundan sonra tarayıcıdaki indirmeler PDM'ye aktarılır, sağ tık menüsünde \"PDM ile indir\" çıkar, videoların üstünde PDM düğmesi görünür. PDM güncellenince eklenti kendini yeniler.\n\nFirefox: \"Firefox'a ekle\" düğmesine basın; Firefox eklentiyi eklemek isteyip istemediğinizi sorar.",
                         "Opera, Chrome and Edge use the same extension; you can load it into several browsers from the same folder.\n\n1. Click your browser's button below (opens its extensions page; if it does not, the address is copied — paste it into the address bar):\n    Opera: opera://extensions   ·   Chrome: chrome://extensions   ·   Edge: edge://extensions\n2. Turn on \"Developer mode\" (Opera and Chrome: top right; Edge: on the left).\n3. Click \"Load unpacked\".\n4. Choose this folder:\n    {0}\n5. The PDM extension appears. Its ID must be: {1}\n\nFrom now on, downloads in the browser are handed to PDM, the context menu gets \"Download with PDM\", and videos show a PDM button. The extension reloads itself when PDM updates.\n\nFirefox: click \"Add to Firefox\"; Firefox asks whether to add the add-on."),
        ["CopyPath"] = ("Klasör yolunu kopyala", "Copy folder path"),
        ["OpenChromeExt"] = ("Chrome", "Chrome"),
        ["OpenEdgeExt"] = ("Edge", "Edge"),
        ["AddToFirefox"] = ("Firefox'a ekle", "Add to Firefox"),
        ["FirefoxMissing"] = ("Firefox bulunamadı.", "Firefox was not found."),
        ["FirefoxNoXpi"] = ("Bu PDM sürümünde Firefox eklentisi yok.", "This PDM build has no Firefox add-on."),
        ["FirefoxUnsigned"] = ("Bu Firefox eklentisi henüz Mozilla tarafından imzalanmadı; normal Firefox onu yalnızca geçici olarak yükler (Firefox kapanınca kalkar).\n\nAçılan about:debugging sayfasında \"Geçici Eklenti Yükle…\" düğmesine basıp şu dosyayı seçin (yolu panoya kopyalandı):\n{0}\n\nKalıcı kurulum için README'deki \"Firefox eklentisini imzalatma\" adımlarına bakın.",
                             "This Firefox add-on is not signed by Mozilla yet; release Firefox only loads it temporarily (until Firefox closes).\n\nOn the about:debugging page that opened, click \"Load Temporary Add-on…\" and choose this file (its path was copied):\n{0}\n\nFor a permanent install see \"Signing the Firefox add-on\" in the README."),
        ["OpenOperaExt"] = ("Opera",
                         "Opera"),
    };

    public static string QueueName(Core.Models.QueueInfo q) => q.Id == Core.Models.QueueInfo.MainId || q.Name.Length == 0 ? T("MainQueue") : q.Name;

    public static string StatusText(Core.Models.DownloadStatus s) => s switch
    {
        Core.Models.DownloadStatus.Queued => T("StQueued"),
        Core.Models.DownloadStatus.Connecting => T("StConnecting"),
        Core.Models.DownloadStatus.Downloading => T("StDownloading"),
        Core.Models.DownloadStatus.Paused => T("StPaused"),
        Core.Models.DownloadStatus.Completed => T("StCompleted"),
        Core.Models.DownloadStatus.Error => T("StError"),
        Core.Models.DownloadStatus.Processing => T("StProcessing"),
        _ => s.ToString(),
    };

    public static string ErrorText(string? code)
    {
        if (string.IsNullOrEmpty(code)) return T("StError");
        if (code.StartsWith("http:"))
        {
            var status = code[5..];
            return status switch
            {
                "401" => T("ErrHttp401"),
                "407" => T("ErrHttp407"),
                "403" => T("ErrHttp403"),
                "404" => T("ErrHttp404"),
                "410" => T("ErrHttp410"),
                _ => F("ErrHttp", status),
            };
        }
        return code switch
        {
            "diskfull" => T("ErrDiskFull"),
            "changed" => T("ErrChanged"),
            "norange" => T("ErrNoRange"),
            "network" => T("ErrNetwork"),
            "fs" => T("ErrFs"),
            "blocked" => T("ErrBlocked"),
            "hash" => T("ErrHash"),
            _ when code.Contains("ytdlp-missing") => T("ErrYtDlpMissing"),
            _ when code.Contains("drm") => T("ErrDrm"),
            _ => F("ErrUnknown", code.StartsWith("unknown:") ? code[8..] : code),
        };
    }
}

/// <summary>XAML: Text="{l:T Key}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
