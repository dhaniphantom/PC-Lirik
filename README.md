# PC-Lirik

Lirik lagu yang melayang di atas aplikasi lain di Windows, sinkron dengan lagu yang sedang diputar. Tidak perlu login atau API key.

Lagu dibaca dari sesi media Windows. Sudah dicoba dengan pemutar di Google Chrome. Kalau beberapa pemutar berjalan bersamaan, aplikasi Spotify didahulukan.

## Fitur

- **Dua baris:** baris yang sedang dinyanyikan dan baris berikutnya.
- **Tembus klik:** klik mouse diteruskan ke aplikasi di belakang lirik.
- **Layar kedua:** lirik tampil di layar kedua, atau di layar utama kalau hanya ada satu layar.
- **Warna karaoke:** warna mengisi huruf dari kiri ke kanan selama baris dinyanyikan.
- **Warna mengikuti lagu:** diambil dari sampul album. Kalau sampul tidak punya warna jelas, dipakai gold.
- **Animasi credit scene:** baris yang selesai bergulir naik sambil memudar.
- **Romanisasi:** lirik Jepang, Korea, Mandarin, Rusia, dan aksara non-Latin lain diubah ke huruf Latin.

Lirik disembunyikan saat lagu dijeda.

## Kebutuhan

- Windows 10 versi 2004 (build 19041) atau lebih baru
- .NET SDK 10 atau lebih baru, dan .NET 10 Desktop Runtime

## Cara pakai

```powershell
git clone https://github.com/dhaniphantom/PC-Lirik
cd PC-Lirik
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.19041.0\LirikMelayang.exe
```

Putar lagu, lalu lirik muncul di bagian bawah layar.

Klik kanan ikon aplikasi di system tray untuk membuka menu:

| Menu | Fungsi |
|---|---|
| Buka kunci / geser | Lirik bisa digeser dengan mouse. Hapus centangnya untuk mengunci lagi. |
| Lirik maju 0,25 dtk | Mempercepat atau memperlambat lirik per 0,1 detik. |
| Keluar | Menutup aplikasi. |

Posisi lirik dan setelan waktu tidak disimpan setelah aplikasi ditutup.

## Keterbatasan

- Hanya lagu yang punya lirik tersinkron di [LRCLIB](https://lrclib.net) yang tampil.
- LRCLIB mencatat waktu per baris. Gerak warna di dalam satu baris adalah perkiraan.
- Di Chrome, Spotify Web dan YouTube tidak bisa dibedakan, jadi video YouTube juga dicarikan lirik.
- Kanji kadang salah baca (二人 menjadi "ninin", seharusnya "futari").
- Nada Mandarin tidak ditampilkan, lagu Kanton dibaca sebagai Mandarin, aksara Arab tampil tanpa vokal, dan hasil bahasa Thai masih kasar.
- Teks kurang terbaca di atas halaman putih.

## Privasi

Judul dan artis lagu yang sedang diputar dikirim ke LRCLIB untuk mencari lirik. Romanisasi dan pembacaan warna sampul berjalan di komputer Anda.

## Pengembangan

| File | Isi |
|---|---|
| `Program.cs` | Jendela, animasi, dan menu tray |
| `Lyrics.cs` | Pengambilan lirik, parser LRC, dan perhitungan waktu |
| `Cover.cs` | Warna dari sampul album |
| `Romanize.cs` | Romanisasi |

Self-test (exit code 0 berarti lolos):

```powershell
$p = Start-Process .\bin\Release\net10.0-windows10.0.19041.0\LirikMelayang.exe --selftest -PassThru -Wait
$p.ExitCode
```

## Lisensi

Kode ini berlisensi [MIT](LICENSE). Kamus dan analisis bahasa Jepang memakai paket [MeCab.DotNet](https://www.nuget.org/packages/MeCab.DotNet) (GPL-2.0 atau LGPL-2.1), yang diunduh dari NuGet saat build.
