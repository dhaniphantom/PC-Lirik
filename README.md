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
- **Kurangi vokal:** vokal di tengah stereo dihilangkan langsung dari suara pemutar, tanpa menyimpan audio.
- **Romanisasi:** lirik Jepang, Korea, Mandarin, Rusia, dan aksara non-Latin lain diubah ke huruf Latin.

Lirik disembunyikan saat lagu dijeda.

## Kebutuhan

- Windows 10 versi 2004 (build 19041) atau lebih baru
- Untuk membangun dari kode: .NET SDK 10 atau lebih baru, dan .NET 10 Desktop Runtime. File exe hasil publish tidak membutuhkan keduanya.

## Cara pakai

```powershell
git clone https://github.com/dhaniphantom/PC-Lirik
cd PC-Lirik
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.19041.0\LirikMelayang.exe
```

Untuk membuat satu file exe (sekitar 91 MB) yang bisa dijalankan tanpa memasang .NET:

```powershell
dotnet publish -c Release -r win-x64 -o publish
```

Hasilnya `publish\LirikMelayang.exe`.

Putar lagu, lalu lirik muncul di bagian bawah layar.

Jendela kontrol muncul di taskbar:

| Kontrol | Fungsi |
|---|---|
| Suara asli / Kurangi vokal (instrumen saja) | Kurangi vokal menghilangkan suara di tengah stereo, biasanya vokal. Berlaku untuk semua suara dari pemutar itu, termasuk tab Chrome lain. Mati sendiri kalau volume pemutar atau perangkat suara diubah. |
| Lebih lambat / Lebih cepat | Menggeser waktu lirik per 0,1 detik, misalnya kalau speaker punya jeda suara. Nilainya disimpan. |
| Geser lirik dengan mouse | Lirik bisa digeser. Hapus centangnya untuk mengunci lagi. |

Perkecil jendela untuk menyembunyikannya ke taskbar. Menutup jendela menutup aplikasi.

Posisi lirik tidak disimpan setelah aplikasi ditutup.

## Keterbatasan

- Hanya lagu yang punya lirik tersinkron di [LRCLIB](https://lrclib.net) yang tampil, dan selisih durasinya dengan lagu yang diputar paling banyak 2 detik.
- Satu lagu bisa punya beberapa versi lirik di LRCLIB. Dipakai versi yang waktunya paling banyak disepakati, dan versi dari album yang sama didahulukan. Pilihannya masih bisa salah.
- LRCLIB mencatat waktu per baris. Gerak warna di dalam satu baris adalah perkiraan.
- Di Chrome, Spotify Web dan YouTube tidak bisa dibedakan, jadi video YouTube juga dicarikan lirik.
- Kanji kadang salah baca (二人 menjadi "ninin", seharusnya "futari").
- Nada Mandarin tidak ditampilkan, lagu Kanton dibaca sebagai Mandarin, aksara Arab tampil tanpa vokal, dan hasil bahasa Thai masih kasar.
- Teks kurang terbaca di atas halaman putih.

- Kurangi vokal bukan pemisahan AI. Sebagian vokal, terutama gema, bisa masih terdengar; snare dan instrumen lain di tengah ikut hilang; hasilnya mono. Instrumen tidak bisa dipisahkan per channel.
- Selama vokal dikurangi, volume pemutar di Volume Mixer diturunkan ke 0,1% dan dikembalikan setelah selesai. Kalau aplikasi tertutup paksa, jalankan lagi saat pemutar terbuka supaya volumenya kembali.

## Privasi

Judul dan artis lagu yang sedang diputar dikirim ke LRCLIB untuk mencari lirik. Romanisasi dan pembacaan warna sampul berjalan di komputer Anda.

## Pengembangan

| File | Isi |
|---|---|
| `Program.cs` | Jendela lirik dan animasi |
| `ControlWindow.cs` | Jendela kontrol di taskbar |
| `Lyrics.cs` | Pengambilan lirik, parser LRC, dan perhitungan waktu |
| `Cover.cs` | Warna dari sampul album |
| `Romanize.cs` | Romanisasi |
| `VocalCut.cs` | Pengurangan vokal |

Self-test (exit code 0 berarti lolos):

```powershell
$p = Start-Process .\bin\Release\net10.0-windows10.0.19041.0\LirikMelayang.exe --selftest -PassThru -Wait
$p.ExitCode
```

## Lisensi

Kode ini berlisensi [MIT](LICENSE). Kamus dan analisis bahasa Jepang memakai paket [MeCab.DotNet](https://www.nuget.org/packages/MeCab.DotNet) (GPL-2.0 atau LGPL-2.1), dan suara memakai [NAudio](https://www.nuget.org/packages/NAudio.Wasapi) (MIT). Keduanya diunduh dari NuGet saat build dan ikut di dalam exe hasil publish.
