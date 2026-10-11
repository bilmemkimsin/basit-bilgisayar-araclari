# Basit Bilgisayar Araçları

Windows 10/11 için Türkçe, yerel çalışan bir WPF araç uygulaması. Uygulama zamanlı kapatma, arşiv çıkarma/birleştirme ve oyun modu profili yerleştirme araçlarını içerir.

## Geliştirme

Gereksinimler: Windows ve .NET 10 SDK.

```powershell
dotnet restore .\BasitBilgisayarAraclari.sln
dotnet build .\BasitBilgisayarAraclari.sln
dotnet test .\BasitBilgisayarAraclari.sln
dotnet run --project .\src\BasitBilgisayarAraclari.App
```

## Windows x64 paketleri

Self-contained taşınabilir paket için:

```powershell
.\scripts\Publish-Release.ps1 -PortableOnly -GeneratedRootName github-release
```

Inno Setup kurulum paketi de üretmek için Inno Setup 6'yı kurup derleyiciyi verin:

```powershell
.\scripts\Publish-Release.ps1 -InnoSetupCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' -GeneratedRootName github-release
```

Üretim çıktıları `.tools/github-release/` altına yazılır ve Git'e eklenmez.

## Kurulum davranışı

Kurulum paketi yönetici izni ister. Kurulum tercihi hedef klasörü etkileyebilir ve kurulum sırasında geçici konsol pencereleri açılabilir. Paketleme akışında dijital imzalama adımı yapılandırılmamıştır.

## Gizlilik ve lisans

Bu kaynak kopyası yerel proje notlarını, makineye özel ortam kayıtlarını, SDK'ları ve derleme/publish çıktısını içermez. Uygulamanın çalışma anında kullanıcı seçtiği profil adları/yolları ve tercihleri yerel `%LOCALAPPDATA%\BasitBilgisayarAraclari` altında tutulabilir.

Projenin özgün kaynak dosyaları MIT lisansı altındadır. Üçüncü taraf bileşenler ve test fixture'ları kendi lisans/attribution bildirimlerine tabidir; RAR fixture lisansı `tests/BasitBilgisayarAraclari.Tests/Fixtures/SharpCompress-TestArchives-LICENSE.txt` içindedir.

