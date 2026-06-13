using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace NoSQLtoSQL
{
    public partial class MainWindow : Window
    {
        private string jsonDosyaYolu = "";
        private string dbYolu = "nosql_to_sql.db";
        private string BaglantiString => $"Data Source={dbYolu};Foreign Keys=False;";
        private JToken jsonRoot;
        private HashSet<string> olusturulanTablolar = new HashSet<string>();
        private List<string> normalizasyonRaporu = new List<string>();
        private Dictionary<string, Dictionary<string, string>> tabloSemalari =
            new Dictionary<string, Dictionary<string, string>>();
        private Dictionary<string, List<string>> anaTabloFKleri =
            new Dictionary<string, List<string>>();
        private Dictionary<string, string> altTabloParentBaglantisi =
            new Dictionary<string, string>();
        private Dictionary<string, string> tabloParentMap =
            new Dictionary<string, string>();
        private Dictionary<string, SqliteCommand> hazirKomutlar =
            new Dictionary<string, SqliteCommand>();

        private int toplamKayitSayisi = 0;
        private int toplamFKSayisi = 0;
        private bool sorguSonucuGosteriliyor = false;

        private readonly StringBuilder logBuffer = new StringBuilder();
        private int logSatirSayisi = 0;
        private const int LOG_FLUSH_ARALIK = 500;

        private const int MAX_PARSE_DERINLIK = 30;
        private const int MAX_AGAC_DERINLIK = 5;
        private const int MAX_SUTUN_ADI_UZUNLUK = 60;

        public MainWindow()
        {
            InitializeComponent();
        }
        private void Log(string mesaj, bool zorlaFlush = false)
        {
            logBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss}] {mesaj}");
            logSatirSayisi++;

            if (zorlaFlush || logSatirSayisi % LOG_FLUSH_ARALIK == 0)
                FlushLog();
        }

        private void FlushLog()
        {
            if (logBuffer.Length == 0) return;
            string metin = logBuffer.ToString();
            logBuffer.Clear();
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText(metin);
                txtLog.ScrollToEnd();
            });
        }
        private void btnDosyaSec_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "JSON Dosyaları (*.json)|*.json";
            if (dialog.ShowDialog() == true)
            {
                jsonDosyaYolu = dialog.FileName;
                string icerik = File.ReadAllText(jsonDosyaYolu);
                jsonRoot = JToken.Parse(icerik);

                treeJson.Items.Clear();
                TreeViewItem kok = new TreeViewItem
                {
                    Header = Path.GetFileName(jsonDosyaYolu),
                    IsExpanded = true
                };
                JsonAgacOlustur(jsonRoot, kok, 0);
                treeJson.Items.Add(kok);

                long boyut = new FileInfo(jsonDosyaYolu).Length;
                txtStatBoyut.Text = boyut < 1024 ? $"{boyut} B" :
                                    boyut < 1048576 ? $"{boyut / 1024.0:F1} KB" :
                                    $"{boyut / 1048576.0:F1} MB";

                txtStatTablo.Text = "0";
                txtStatKayit.Text = "0";
                txtStatFK.Text = "0";
                txtStatSure.Text = "0 ms";

                btnDonustur.IsEnabled = true;
                Log("JSON dosyasi yuklendi: " + jsonDosyaYolu, true);
            }
        }

        private void JsonAgacOlustur(JToken token, TreeViewItem parentNode, int derinlik)
        {
            if (derinlik > MAX_AGAC_DERINLIK)
            {
                parentNode.Items.Add(new TreeViewItem { Header = "... (daha fazla)" });
                return;
            }

            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    TreeViewItem node = new TreeViewItem { IsExpanded = derinlik < 2 };
                    if (prop.Value is JObject || prop.Value is JArray)
                        node.Header = "[Nesne] " + prop.Name;
                    else
                        node.Header = prop.Name + ": " + prop.Value.ToString();
                    JsonAgacOlustur(prop.Value, node, derinlik + 1);
                    parentNode.Items.Add(node);
                }
            }
            else if (token is JArray arr)
            {
                int i = 0;
                foreach (var item in arr)
                {
                    TreeViewItem node = new TreeViewItem
                    {
                        Header = "[" + i + "]",
                        IsExpanded = derinlik < 2
                    };
                    JsonAgacOlustur(item, node, derinlik + 1);
                    parentNode.Items.Add(node);
                    i++;
                }
            }
        }
        private string SqlTipiBelirle(JToken token)
        {
            if (token.Type == JTokenType.Integer) return "INTEGER";
            if (token.Type == JTokenType.Float) return "REAL";
            if (token.Type == JTokenType.Boolean) return "INTEGER";
            if (token.Type == JTokenType.Null) return "NUMERIC";

            string deger = token.ToString();
            if (Regex.IsMatch(deger, @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2})?$"))
                return "DATETIME";
            if (deger.Length <= 50) return "VARCHAR(50)";
            if (deger.Length <= 255) return "VARCHAR(255)";
            return "TEXT";
        }

        private string DegerDonustur(JToken token)
        {
            if (token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean) return (bool)token ? "1" : "0";
            return token.ToString();
        }

        private string TipBirlestir(string mevcutTip, string yeniTip)
        {
            if (mevcutTip == yeniTip) return mevcutTip;
            if (mevcutTip == "TEXT" || yeniTip == "TEXT") return "TEXT";
            if (mevcutTip == "VARCHAR(255)" || yeniTip == "VARCHAR(255)") return "VARCHAR(255)";
            if (mevcutTip == "VARCHAR(50)" || yeniTip == "VARCHAR(50)") return "VARCHAR(50)";
            if (mevcutTip == "REAL" || yeniTip == "REAL") return "REAL";
            if (mevcutTip == "NUMERIC" || yeniTip == "NUMERIC") return "NUMERIC";
            return mevcutTip;
        }
        private bool IcObjAyriTabloyaMi(JObject obj)
        {
            foreach (var prop in obj.Properties())
                if (prop.Value is JObject || prop.Value is JArray)
                    return true;
            return false;
        }
        private void PreScan(JToken token, string tabloAdi, bool parentIdVar,
            string parentTabloAdi, int derinlik = 0)
        {
            if (derinlik > MAX_PARSE_DERINLIK)
            {
                Log($"Maksimum derinlik ({MAX_PARSE_DERINLIK}) asildi, '{tabloAdi}' atlandi.");
                return;
            }

            if (token is JObject obj)
            {
                if (!tabloSemalari.ContainsKey(tabloAdi))
                    tabloSemalari[tabloAdi] = new Dictionary<string, string>();
                if (!anaTabloFKleri.ContainsKey(tabloAdi))
                    anaTabloFKleri[tabloAdi] = new List<string>();

                if (parentIdVar)
                {
                    tabloSemalari[tabloAdi]["parent_id"] = "INTEGER";
                    if (!string.IsNullOrEmpty(parentTabloAdi))
                        tabloParentMap[tabloAdi] = parentTabloAdi;
                }

                foreach (var prop in obj.Properties())
                {
                    string ad = TemizleAd(prop.Name);
                    if (string.IsNullOrEmpty(ad) || ad == "id") continue;

                    if (prop.Value is JObject icObj)//3NF
                    {
                        if (IcObjAyriTabloyaMi(icObj))
                        {
                            string altTabloAdi = tabloAdi + "_" + ad;
                            tabloSemalari[tabloAdi][ad + "_id"] = "INTEGER";
                            string fkGirdisi = ad + "_id|" + altTabloAdi;
                            if (!anaTabloFKleri[tabloAdi].Contains(fkGirdisi))
                                anaTabloFKleri[tabloAdi].Add(fkGirdisi);
                            tabloParentMap[altTabloAdi] = tabloAdi;
                            PreScan(icObj, altTabloAdi, false, tabloAdi, derinlik + 1);
                            normalizasyonRaporu.Add(
                                $"3NF: '{prop.Name}' derin obje -> '{altTabloAdi}' tablosuna tasindi, FK: {ad}_id");
                        }
                        else
                        {
                            DuzlestirPreScan(icObj, ad, tabloAdi, derinlik + 1);
                            normalizasyonRaporu.Add(
                                $"FLAT: '{prop.Name}' sig obje -> '{tabloAdi}' tablosunda duzlestirildi");
                        }
                    }
                    else if (prop.Value is JArray arr)
                    {
                        string altTabloAdi = tabloAdi + "_" + ad;
                        altTabloParentBaglantisi[altTabloAdi] = tabloAdi;
                        tabloParentMap[altTabloAdi] = tabloAdi;
                        normalizasyonRaporu.Add(
                            $"1NF: '{prop.Name}' dizisi -> '{altTabloAdi}' alt tablosuna tasindi");//1NF

                        foreach (var eleman in arr)
                        {
                            if (eleman is JObject)
                                PreScan(eleman, altTabloAdi, true, tabloAdi, derinlik + 1);
                            else
                            {
                                if (!tabloSemalari.ContainsKey(altTabloAdi))
                                    tabloSemalari[altTabloAdi] = new Dictionary<string, string>();
                                tabloSemalari[altTabloAdi]["parent_id"] = "INTEGER";
                                if (!tabloSemalari[altTabloAdi].ContainsKey("deger"))
                                    tabloSemalari[altTabloAdi]["deger"] = SqlTipiBelirle(eleman);
                                else
                                    tabloSemalari[altTabloAdi]["deger"] =
                                        TipBirlestir(tabloSemalari[altTabloAdi]["deger"], SqlTipiBelirle(eleman));
                            }
                        }
                    }
                    else
                    {
                        string yeniTip = SqlTipiBelirle(prop.Value);
                        if (!tabloSemalari[tabloAdi].ContainsKey(ad))
                            tabloSemalari[tabloAdi][ad] = yeniTip;
                        else
                            tabloSemalari[tabloAdi][ad] = TipBirlestir(tabloSemalari[tabloAdi][ad], yeniTip);
                    }
                }
            }
            else if (token is JArray arr2)
            {
                foreach (var item in arr2)
                    PreScan(item, tabloAdi, parentIdVar, parentTabloAdi, derinlik + 1);
            }
        }

        private void DuzlestirPreScan(JObject obj, string prefix, string tabloAdi, int derinlik = 0)
        {
            if (derinlik > MAX_PARSE_DERINLIK) return;

            foreach (var prop in obj.Properties())
            {
                string ad = TemizleAd(prefix + "_" + prop.Name);
                if (ad.Length > MAX_SUTUN_ADI_UZUNLUK)
                    ad = ad.Substring(0, MAX_SUTUN_ADI_UZUNLUK);

                if (prop.Value is JObject ic)
                {
                    if (IcObjAyriTabloyaMi(ic))
                    {
                        tabloSemalari[tabloAdi][ad + "_id"] = "INTEGER";
                        string fkGirdisi = ad + "_id|" + tabloAdi + "_" + ad;
                        if (!anaTabloFKleri[tabloAdi].Contains(fkGirdisi))
                            anaTabloFKleri[tabloAdi].Add(fkGirdisi);
                        tabloParentMap[tabloAdi + "_" + ad] = tabloAdi;
                        PreScan(ic, tabloAdi + "_" + ad, false, tabloAdi, derinlik + 1);
                        normalizasyonRaporu.Add(
                            $"3NF: Flatten icinde derin obje '{prop.Name}' -> '{tabloAdi}_{ad}' tablosuna tasindi");
                    }
                    else
                        DuzlestirPreScan(ic, ad, tabloAdi, derinlik + 1);
                }
                else if (!(prop.Value is JArray))
                {
                    string yeniTip = SqlTipiBelirle(prop.Value);
                    if (!tabloSemalari[tabloAdi].ContainsKey(ad))
                        tabloSemalari[tabloAdi][ad] = yeniTip;
                    else
                        tabloSemalari[tabloAdi][ad] = TipBirlestir(tabloSemalari[tabloAdi][ad], yeniTip);
                }
            }
        }
        private void TumTablolariOlustur(SqliteConnection conn, SqliteTransaction transaction)
        {
            var sirali = SiraliTablolar();

            foreach (var tabloAdi in sirali)
            {
                if (olusturulanTablolar.Contains(tabloAdi)) continue;
                if (!tabloSemalari.ContainsKey(tabloAdi)) continue;
                olusturulanTablolar.Add(tabloAdi);

                var tablo = tabloSemalari[tabloAdi];
                var sb = new StringBuilder();
                sb.Append($"CREATE TABLE IF NOT EXISTS [{tabloAdi}] (id INTEGER PRIMARY KEY AUTOINCREMENT");//2NF

                foreach (var sutun in tablo)
                {
                    if (sutun.Key == "id") continue;
                    sb.Append($", [{sutun.Key}] {sutun.Value}");
                }

                if (anaTabloFKleri.ContainsKey(tabloAdi))
                {
                    foreach (var fk in anaTabloFKleri[tabloAdi])
                    {
                        var parcalar = fk.Split('|');
                        sb.Append($", FOREIGN KEY ([{parcalar[0]}]) REFERENCES [{parcalar[1]}](id)");
                        normalizasyonRaporu.Add(
                            $"2NF: '{tabloAdi}' tablosuna FK eklendi: {parcalar[0]} -> {parcalar[1]}(id)");
                        toplamFKSayisi++;
                    }
                }

                if (tablo.ContainsKey("parent_id") && altTabloParentBaglantisi.ContainsKey(tabloAdi))
                {
                    string parentTablo = altTabloParentBaglantisi[tabloAdi];
                    sb.Append($", FOREIGN KEY (parent_id) REFERENCES [{parentTablo}](id)");
                    normalizasyonRaporu.Add(
                        $"2NF: '{tabloAdi}' alt tablosuna parent_id FK eklendi -> {parentTablo}(id)");
                    toplamFKSayisi++;
                }

                sb.Append(");");
                using var cmd = new SqliteCommand(sb.ToString(), conn, transaction);
                cmd.ExecuteNonQuery();
                Log("Tablo olusturuldu: " + tabloAdi);
            }
        }

        private List<string> SiraliTablolar()
        {
            var sirali = new List<string>();
            var ziyaretEdilen = new HashSet<string>();

            void Ekle(string tablo)
            {
                if (ziyaretEdilen.Contains(tablo)) return;
                ziyaretEdilen.Add(tablo);
                if (tabloParentMap.ContainsKey(tablo))
                    Ekle(tabloParentMap[tablo]);
                sirali.Add(tablo);
            }

            foreach (var tablo in tabloSemalari.Keys)
                Ekle(tablo);

            return sirali;
        }
        private async void btnDonustur_Click(object sender, RoutedEventArgs e)
        {
            btnDonustur.IsEnabled = false;
            btnSifirla.IsEnabled = false;

            try
            {
                olusturulanTablolar.Clear();
                normalizasyonRaporu.Clear();
                tabloSemalari.Clear();
                anaTabloFKleri.Clear();
                altTabloParentBaglantisi.Clear();
                tabloParentMap.Clear();
                hazirKomutlar.Clear();
                toplamKayitSayisi = 0;
                toplamFKSayisi = 0;
                logBuffer.Clear();
                logSatirSayisi = 0;

                if (File.Exists(dbYolu)) File.Delete(dbYolu);

                string anaTabloAdi;
                JToken taranacakToken;

                if (jsonRoot is JObject rootObj && rootObj.Count == 1)
                {
                    var ilkProp = rootObj.Properties().First();
                    anaTabloAdi = TemizleAd(ilkProp.Name);
                    taranacakToken = ilkProp.Value;
                }
                else
                {
                    anaTabloAdi = TemizleAd(
                        Path.GetFileNameWithoutExtension(jsonDosyaYolu).ToLower());
                    taranacakToken = jsonRoot;
                }

                var sw = Stopwatch.StartNew();

                await Task.Run(() =>
                {
                    Log("JSON semasi taraniyor...");
                    PreScan(taranacakToken, anaTabloAdi, false, "");
                    Log("Sema kesfi tamamlandi.", true);

                    using var conn = new SqliteConnection(BaglantiString);
                    conn.Open();
                    ExecutePragmas(conn);

                    using (var transaction = conn.BeginTransaction())
                    {
                        TumTablolariOlustur(conn, transaction);
                        transaction.Commit();
                    }
                    FlushLog();
                    Log("Veritabani ve tablolar olusturuldu.", true);

                    using (var transaction = conn.BeginTransaction())
                    {
                        if (taranacakToken is JArray dizi)
                        {
                            int i = 0;
                            foreach (var item in dizi)
                            {
                                if (item is JObject obj2)
                                    JsonDonustur(obj2, anaTabloAdi, conn, transaction, -1);

                                i++;
                                if (i % 1000 == 0)
                                    Log($"{i} kayit islendi...", true);
                            }
                        }
                        else if (taranacakToken is JObject tek)
                        {
                            JsonDonustur(tek, anaTabloAdi, conn, transaction, -1);
                        }

                        transaction.Commit();
                    }

                    FlushLog();
                    sw.Stop();

                    Log("Donusum tamamlandi!", true);
                    Log("");
                    Log("========== NORMALIZASYON RAPORU ==========");
                    Log("1NF (Birinci Normal Form):");
                    Log("   -> Tum dizi yapilari ayri alt tablolara tasindi.");
                    Log("   -> Her hucre atomik deger iceriyor.");
                    foreach (var r in normalizasyonRaporu)
                        if (r.StartsWith("1NF")) Log("   -> " + r.Substring(4));

                    Log("2NF (Ikinci Normal Form):");
                    Log("   -> Her tabloya otomatik PRIMARY KEY (id) atandi.");
                    Log("   -> Alt tablolar FOREIGN KEY ile baglandi.");
                    foreach (var r in normalizasyonRaporu)
                        if (r.StartsWith("2NF")) Log("   -> " + r.Substring(4));

                    Log("3NF (Ucuncu Normal Form):");
                    Log("   -> Derin ic ice objeler ayri tablolara tasindi.");
                    Log("   -> Sig objeler flattening ile duzlestirildi.");
                    foreach (var r in normalizasyonRaporu)
                        if (r.StartsWith("3NF")) Log("   -> " + r.Substring(4));

                    Log("Flattening (Duzlestirme):");
                    foreach (var r in normalizasyonRaporu)
                        if (r.StartsWith("FLAT")) Log("   -> " + r.Substring(5));

                    Log("==========================================");
                    Log($"Istatistik -> Tablo: {olusturulanTablolar.Count} | " +
                        $"Kayit: {toplamKayitSayisi} | FK: {toplamFKSayisi} | " +
                        $"Sure: {sw.ElapsedMilliseconds} ms", true);

                    Dispatcher.Invoke(() =>
                    {
                        txtStatTablo.Text = olusturulanTablolar.Count.ToString();
                        txtStatKayit.Text = toplamKayitSayisi.ToString();
                        txtStatFK.Text = toplamFKSayisi.ToString();
                        txtStatSure.Text = $"{sw.ElapsedMilliseconds} ms";
                    });

                    using var connRead = new SqliteConnection(BaglantiString);
                    connRead.Open();
                    Dispatcher.Invoke(() => TablolariYukle(connRead));
                });
            }
            catch (Exception ex)
            {
                Log("Hata: " + ex.Message, true);
            }
            finally
            {
                btnDonustur.IsEnabled = true;
                btnSifirla.IsEnabled = true;
            }
        }

        private void ExecutePragmas(SqliteConnection conn)
        {
            var pragmalar = new[]
            {
                "PRAGMA journal_mode=WAL;",
                "PRAGMA synchronous=NORMAL;",
                "PRAGMA cache_size=-65536;",
                "PRAGMA temp_store=MEMORY;",
                "PRAGMA mmap_size=268435456;",
                "PRAGMA page_size=4096;",
                "PRAGMA foreign_keys=OFF;",
            };
            foreach (var p in pragmalar)
                new SqliteCommand(p, conn).ExecuteNonQuery();
        }
        private long JsonDonustur(JObject obj, string tabloAdi, SqliteConnection conn,
            SqliteTransaction transaction, long parentId, int derinlik = 0)
        {
            if (derinlik > MAX_PARSE_DERINLIK)
                return -1;

            var sutunlar = new Dictionary<string, string>();
            var altDiziler = new Dictionary<string, JArray>();
            var altObjeler = new Dictionary<string, (JObject obj, bool ayriTabloya)>();

            foreach (var prop in obj.Properties())
            {
                string ad = TemizleAd(prop.Name);
                if (string.IsNullOrEmpty(ad) || ad == "id") continue;
                if (ad.Length > MAX_SUTUN_ADI_UZUNLUK)
                    ad = ad.Substring(0, MAX_SUTUN_ADI_UZUNLUK);

                if (prop.Value is JObject icObj)
                    altObjeler[ad] = (icObj, IcObjAyriTabloyaMi(icObj));
                else if (prop.Value is JArray arr)
                    altDiziler[ad] = arr;
                else
                    sutunlar[ad] = DegerDonustur(prop.Value);
            }

            foreach (var altObj in altObjeler)
            {
                if (altObj.Value.ayriTabloya)
                {
                    string altTabloAdi = tabloAdi + "_" + altObj.Key;
                    long altId = JsonDonustur(altObj.Value.obj, altTabloAdi, conn, transaction, -1, derinlik + 1);
                    if (altId >= 0)
                        sutunlar[altObj.Key + "_id"] = altId.ToString();
                }
                else
                {
                    DuzlestirVeri(altObj.Value.obj, altObj.Key, sutunlar, tabloAdi, conn, transaction, derinlik + 1);
                }
            }

            long yeniId = VeriEkle(tabloAdi, sutunlar, parentId, conn, transaction);
            toplamKayitSayisi++;

            foreach (var dizi in altDiziler)
            {
                string altTabloAdi = tabloAdi + "_" + dizi.Key;
                foreach (var eleman in dizi.Value)
                {
                    if (eleman is JObject eObj)
                        JsonDonustur(eObj, altTabloAdi, conn, transaction, yeniId, derinlik + 1);
                    else
                    {
                        var basit = new Dictionary<string, string>
                        {
                            { "deger", DegerDonustur(eleman) }
                        };
                        VeriEkle(altTabloAdi, basit, yeniId, conn, transaction);
                        toplamKayitSayisi++;
                    }
                }
            }

            return yeniId;
        }

        private void DuzlestirVeri(JObject obj, string prefix, Dictionary<string, string> sutunlar,
            string tabloAdi, SqliteConnection conn, SqliteTransaction transaction, int derinlik = 0)
        {
            if (derinlik > MAX_PARSE_DERINLIK) return;

            foreach (var prop in obj.Properties())
            {
                string ad = prefix + "_" + TemizleAd(prop.Name);
                if (ad.Length > MAX_SUTUN_ADI_UZUNLUK)
                    ad = ad.Substring(0, MAX_SUTUN_ADI_UZUNLUK);

                if (prop.Value is JObject ic)
                {
                    if (IcObjAyriTabloyaMi(ic))
                    {
                        string altTabloAdi = tabloAdi + "_" + ad;
                        long altId = JsonDonustur(ic, altTabloAdi, conn, transaction, -1, derinlik + 1);
                        if (altId >= 0)
                            sutunlar[ad + "_id"] = altId.ToString();
                    }
                    else
                        DuzlestirVeri(ic, ad, sutunlar, tabloAdi, conn, transaction, derinlik + 1);
                }
                else if (!(prop.Value is JArray))
                    sutunlar[ad] = DegerDonustur(prop.Value);
            }
        }
        private long VeriEkle(string tabloAdi, Dictionary<string, string> sutunlar, long parentId,
            SqliteConnection conn, SqliteTransaction transaction)
        {
            var sutunListesi = new List<string>(sutunlar.Keys);
            sutunListesi.Sort();
            bool parentVar = tabloSemalari.ContainsKey(tabloAdi) &&
                             tabloSemalari[tabloAdi].ContainsKey("parent_id");

            string cacheKey = tabloAdi + "|" + (parentVar ? "P|" : "") + string.Join(",", sutunListesi);

            if (!hazirKomutlar.TryGetValue(cacheKey, out SqliteCommand cmd))
            {
                var adlar = new List<string>();
                var degerler = new List<string>();

                if (parentVar)
                {
                    adlar.Add("parent_id");
                    degerler.Add("@parent_id");
                }

                foreach (var s in sutunListesi)
                {
                    if (s == "id") continue;
                    adlar.Add($"[{s}]");
                    degerler.Add("@param_" + s);
                }

                string sqlInsert = adlar.Count == 0
                    ? $"INSERT INTO [{tabloAdi}] DEFAULT VALUES; SELECT last_insert_rowid();"
                    : $"INSERT INTO [{tabloAdi}] ({string.Join(", ", adlar)}) VALUES ({string.Join(", ", degerler)}); SELECT last_insert_rowid();";

                cmd = new SqliteCommand(sqlInsert, conn, transaction);

                if (parentVar)
                    cmd.Parameters.Add("@parent_id", SqliteType.Integer);

                foreach (var s in sutunListesi)
                {
                    if (s == "id") continue;
                    cmd.Parameters.Add("@param_" + s, SqliteType.Text);
                }

                hazirKomutlar[cacheKey] = cmd;
            }
            else
            {
                cmd.Connection = conn;
                cmd.Transaction = transaction;
            }

            if (parentVar)
                cmd.Parameters["@parent_id"].Value = parentId >= 0 ? (object)parentId : DBNull.Value;

            foreach (var s in sutunlar)
            {
                if (s.Key == "id") continue;
                cmd.Parameters["@param_" + s.Key].Value = (object)s.Value ?? DBNull.Value;
            }

            return (long)cmd.ExecuteScalar();
        }
        private void TablolariYukle(SqliteConnection conn)
        {
            cmbTablolar.Items.Clear();
            using var reader = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;", conn).ExecuteReader();
            while (reader.Read()) cmbTablolar.Items.Add(reader.GetString(0));
            if (cmbTablolar.Items.Count > 0) cmbTablolar.SelectedIndex = 0;
        }

        private void cmbTablolar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTablolar.SelectedItem == null) return;
            if (sorguSonucuGosteriliyor) return;
            string tablo = cmbTablolar.SelectedItem.ToString();

            using var conn = new SqliteConnection(BaglantiString);
            conn.Open();
            using var cmd = new SqliteCommand($"SELECT * FROM [{tablo}] LIMIT 1000;", conn);
            using var reader = cmd.ExecuteReader();
            var dt = new DataTable();
            dt.Load(reader);
            dataGrid.ItemsSource = dt.DefaultView;
        }

        private void btnSorguCalistir_Click(object sender, RoutedEventArgs e)
        {
            string sorgu = txtSorgu.Text.Trim();
            if (string.IsNullOrEmpty(sorgu))
            {
                Log("Sorgu bos olamaz.", true);
                return;
            }

            if (!File.Exists(dbYolu))
            {
                Log("Once bir JSON dosyasi donusturun.", true);
                return;
            }

            try
            {
                using var conn = new SqliteConnection(BaglantiString);
                conn.Open();
                using var cmd = new SqliteCommand(sorgu, conn);

                if (sorgu.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = cmd.ExecuteReader();
                    var dt = new DataTable();
                    dt.Load(reader);
                    dataGrid.ItemsSource = dt.DefaultView;

                    var fromEsles = Regex.Match(sorgu, @"FROM\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
                    if (fromEsles.Success)
                    {
                        string sorguTablo = fromEsles.Groups[1].Value;
                        if (cmbTablolar.Items.Contains(sorguTablo))
                        {
                            sorguSonucuGosteriliyor = true;
                            cmbTablolar.SelectedItem = sorguTablo;
                            sorguSonucuGosteriliyor = false;
                        }
                    }

                    Log($"Sorgu calistirildi -> {dt.Rows.Count} satir dondu.", true);
                }
                else
                {
                    int etkilenen = cmd.ExecuteNonQuery();
                    Log($"Sorgu calistirildi -> {etkilenen} satir etkilendi.", true);
                }
            }
            catch (Exception ex)
            {
                Log("Sorgu hatasi: " + ex.Message, true);
            }
        }

        private void btnSorguTemizle_Click(object sender, RoutedEventArgs e)
        {
            txtSorgu.Text = "SELECT * FROM ";
            txtSorgu.Focus();
            txtSorgu.CaretIndex = txtSorgu.Text.Length;
        }

        private void btnSifirla_Click(object sender, RoutedEventArgs e)
        {
            treeJson.Items.Clear();
            cmbTablolar.Items.Clear();
            dataGrid.ItemsSource = null;
            txtLog.Clear();
            jsonDosyaYolu = "";
            btnDonustur.IsEnabled = false;
            jsonRoot = null;
            logBuffer.Clear();
            logSatirSayisi = 0;

            txtStatTablo.Text = "0";
            txtStatKayit.Text = "0";
            txtStatFK.Text = "0";
            txtStatSure.Text = "0 ms";
            txtStatBoyut.Text = "-";

            olusturulanTablolar.Clear();
            normalizasyonRaporu.Clear();
            tabloSemalari.Clear();
            anaTabloFKleri.Clear();
            altTabloParentBaglantisi.Clear();
            tabloParentMap.Clear();
            hazirKomutlar.Clear();
            toplamKayitSayisi = 0;
            toplamFKSayisi = 0;

            if (File.Exists(dbYolu))
            {
                try
                {
                    using (var conn = new SqliteConnection(BaglantiString))
                    {
                        conn.Open();
                        new SqliteCommand("PRAGMA foreign_keys = OFF;", conn).ExecuteNonQuery();

                        var tablolar = new List<string>();
                        using (var reader = new SqliteCommand(
                            "SELECT name FROM sqlite_master WHERE type='table';", conn).ExecuteReader())
                        {
                            while (reader.Read())
                                tablolar.Add(reader.GetString(0));
                        }

                        foreach (var tablo in tablolar)
                        {
                            if (tablo == "sqlite_sequence") continue;
                            new SqliteCommand($"DROP TABLE IF EXISTS [{tablo}];", conn).ExecuteNonQuery();
                        }
                    }

                    SqliteConnection.ClearAllPools();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    System.Threading.Thread.Sleep(150);
                    if (File.Exists(dbYolu)) File.Delete(dbYolu);

                    Log("Veritabani temizlendi.", true);
                }
                catch (Exception ex)
                {
                    Log("Veritabani silinemedi: " + ex.Message, true);
                }
            }

            Log("Sistem sifirlandi. Yeni JSON dosyasi secebilirsiniz.", true);
        }
        private string TemizleAd(string ad)
        {
            if (string.IsNullOrEmpty(ad)) return "tablo";
            string temiz = ad
                .Replace('i', 'i').Replace('I', 'i')
                .Replace('s', 's').Replace('S', 's')
                .Replace('g', 'g').Replace('G', 'g')
                .Replace('c', 'c').Replace('C', 'c')
                .Replace('o', 'o').Replace('O', 'o')
                .Replace('u', 'u').Replace('U', 'u')
                .Replace('ı', 'i').Replace('İ', 'i')
                .Replace('ş', 's').Replace('Ş', 's')
                .Replace('ğ', 'g').Replace('Ğ', 'g')
                .Replace('ç', 'c').Replace('Ç', 'c')
                .Replace('ö', 'o').Replace('Ö', 'o')
                .Replace('ü', 'u').Replace('Ü', 'u');

            string sonuc = Regex.Replace(temiz, @"[^a-zA-Z0-9_]", "_").ToLower();
            if (sonuc == "id")
                sonuc = "id_alan";

            return sonuc;
        }
    }
}