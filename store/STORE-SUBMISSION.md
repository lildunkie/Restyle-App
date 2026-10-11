# Publishing Restyle to the Microsoft Store

The Store edition is a separate project (`src/Restyle.Store`) built from the same source as the
download version, so fixes go into both. Differences in the Store edition:

- "Start the dock when I sign in" uses a Store **startup task** (shown in Settings > Apps > Startup)
  instead of the registry Run key.
- The "Open built-in presets folder" button is hidden (Store apps live in a protected folder).
- It's packaged as an `.msix`; Microsoft signs it, installs it in one click and keeps it updated.

## 1. Create your Partner Center account (one time)

1. Go to <https://partner.microsoft.com/dashboard> and sign up as an **Individual** developer
   (free for individuals; you'll verify your identity).
2. **Apps and games > New product > MSIX or PWA app**, and reserve a name. "Restyle" may already
   be taken. If so, try something like "Restyle Desktop" or "Restyle for Windows". The name is only
   the Store listing; the app can still call itself Restyle.

## 2. Build the package with your identity

In Partner Center open your app > **Product management > Product identity**, and copy:

| Partner Center field | Script parameter |
|---|---|
| Package/Identity/Name | `-IdentityName` |
| Package/Identity/Publisher | `-Publisher` |
| Package/Properties/PublisherDisplayName | `-PublisherDisplayName` |

Then, from the `Restyle` folder:

```powershell
powershell -ExecutionPolicy Bypass -File store\build-msix.ps1 `
    -IdentityName "PASTE.Name" -Publisher "CN=PASTE-PUBLISHER-ID" `
    -PublisherDisplayName "PASTE NAME" -Version 1.0.0.0
```

The package lands in `out\store\Restyle_1.0.0.0_x64.msix`. For each update, raise the version
(1.0.1.0, 1.1.0.0, …; the last number must stay 0).

## 3. Create the submission

**Pricing and availability:** Free (or your choice), all markets.

**Properties:** Category *Personalization*. "This product accesses, collects or transmits personal
information": **No** (Restyle makes no network connections and collects nothing).

**Age ratings:** complete the questionnaire. No user-generated content, no communication, no
purchases. It should come out as suitable for everyone.

**Packages:** upload the `.msix`. Partner Center will flag the two restricted capabilities;
paste this justification in **Submission options > Restricted capabilities**:

> **runFullTrust:** Restyle is a desktop (WPF) personalization app. It changes Windows appearance
> settings through documented Win32 APIs (SystemParametersInfo, SHAppBarMessage, Core Audio, WMI
> brightness) and provides an optional dock and top bar, which needs a full-trust desktop process.
>
> **unvirtualizedResources:** Restyle's purpose is changing the user's real Windows personalization
> settings (light/dark mode, accent color, taskbar options, mouse pointers, desktop icons) stored under
> HKEY_CURRENT_USER, and saving wallpaper/cursor files that Windows itself must read. With registry and
> file-system write virtualization enabled these changes would go to a private copy and have no
> effect. Restyle only writes per-user settings (no HKLM, no admin rights, no system files), records
> every change so it can be undone, and offers "Restore my original settings".

**Store listing (English):**

*Description:*

> Give Windows a fresh look in one click. Restyle can make your PC feel like macOS, GNOME, KDE Plasma,
> Ubuntu, Linux Mint, Pop!_OS or elementary OS, or you can fine-tune every detail yourself.
>
> • One-click presets that show exactly what will change before you apply them
> • Light/dark mode, accent colors, transparency, taskbar layout, mouse pointers and desktop icons
> • Nine original wallpapers, drawn at your screen's resolution
> • An optional Mac-style dock with hover zoom, plus a top bar with clock, Wi-Fi, Bluetooth, volume,
>   brightness and battery controls
> • Safe by design: your original settings are saved on first launch, every change can be undone,
>   and Restyle never modifies Windows system files or needs administrator rights
> • Private: everything stays on your PC. No accounts, no ads, no tracking, no internet access

*Short description:* Make Windows look like macOS or Linux, with a dock and top bar. Every change can be undone.

*Features (one per line):* One-click desktop presets · Mac-style dock · Top bar and control center ·
Accent colors and dark mode · Taskbar customization · Cursor packs · Original wallpapers · Undo and backups

*Keywords:* theme, dock, taskbar, macOS, Linux, dark mode, customization

*Screenshots:* at least one, 1366×768 or larger (PNG). Good ones: the Presets page, the dock and
top bar on your desktop, the control center.

*Privacy policy URL:* optional when no personal data is collected, but recommended. A one-paragraph
page (for example on GitHub) saying "Restyle collects no data and makes no network connections" is enough.

**Notes for certification** (helps reviewers):

> To see the dock and top bar: open Restyle > Dock > Turn dock on. "Completely hide the Windows
> taskbar" is optional and the taskbar returns as soon as the dock is turned off. All changes can be
> reverted with Backups > Restore my original settings.

Submit. Certification usually takes from a few hours to a few days.

## Test on your PC before submitting (optional)

Installing an `.msix` outside the Store requires it to be signed by a certificate your PC trusts:

```powershell
powershell -ExecutionPolicy Bypass -File store\build-msix.ps1 -TestSign
```

This creates a self-signed certificate (`out\store\Restyle-test.cer`) and signs the package with it.
Then, **once**, trust the certificate (needs admin): double-click `Restyle-test.cer` > Install
Certificate > Local Machine > "Place all certificates in the following store" > **Trusted People**.
Now double-click the `.msix` to install. Uninstall it from Settings > Apps before installing the Store
version, and remove the test certificate afterwards if you like (`certlm.msc` > Trusted People).
