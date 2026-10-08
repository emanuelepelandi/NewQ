# libVLC DirectShow module

`win-x64/plugins/access/libdshow_plugin.dll` is the DirectShow capture input of libVLC (used by Live cues:
cameras and capture cards). The NuGet package `VideoLAN.LibVLC.Windows` 3.0.21 does not ship it, so it is taken
unchanged from the official VLC 3.0.21 Windows 64-bit build:

- source: https://get.videolan.org/vlc/3.0.21/win64/vlc-3.0.21-win64.zip
- archive SHA-256: a0b7ec02b50adf6417eed014fb8df50af39690505a4225b85b3dc2ed17d14843 (matches VideoLAN's .sha256)
- file: `vlc-3.0.21/plugins/access/libdshow_plugin.dll`, Authenticode-signed by VideoLAN (Paris, FR)

License: LGPL 2.1 or later (see `COPYING-VLC.txt`). Keep it at the same version as the libVLC package.
