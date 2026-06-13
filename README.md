# JSON to SQL Converter

JSON dosyalarını otomatik olarak normalize edilmiş SQLite veritabanlarına dönüştüren WPF masaüstü uygulaması.

## Özellikler
- Dinamik şema çıkarımı (hardcoded tablo yok)
- 1NF, 2NF, 3NF normalizasyonu
- DFS tabanlı topolojik sıralama
- Memoization ile optimize INSERT performansı

## Kullanılan Teknolojiler
- C# / WPF / .NET
- SQLite
- Newtonsoft.Json

## Nasıl Çalıştırılır?
1. Repoyu indir
2. Visual Studio'da `NoSQLtoSQL.sln` dosyasını aç
3. Çalıştır (F5)
