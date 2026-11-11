# Script Runner app (Windows, macOS, Android)

The Script Runner app does what the browser page does (upload a script, see its output live, type input, Stop
with Ctrl+C, and hear `say()`, play sounds, and see images), with one addition: it can connect two ways.

| Connection | What the Pi needs | Sign-in |
| :--- | :--- | :--- |
| **SSH** | Nothing beyond SSH and Python 3, which Raspberry Pi OS has | The Pi account's user name and password |
| **HTTP** | The runner server from this repository ([installation.md](installation.md)) | The runner's PIN |

Choose the connection in the drop-down at the top left.

## Install

Download the file for your computer from the repository's **Releases** page.

| File | For | First start |
| :--- | :--- | :--- |
| `ScriptRunner-Windows.exe` | Windows 10 and 11 | Windows may show "Windows protected your PC". Choose **More info**, then **Run anyway**. |
| `ScriptRunner-Mac-AppleSilicon.zip` | Macs with an M-series chip | Unzip, move **Script Runner** to Applications, then open it with right-click > **Open** and confirm. On recent macOS versions, if it is still blocked, open **System Settings > Privacy & Security** and choose **Open Anyway**. |
| `ScriptRunner-Mac-Intel.zip` | Macs with an Intel chip | As above |
| `ScriptRunner-Android.apk` | Android 7 or newer | Open the file on the phone and allow installing apps from that source when Android asks. |

The apps are not signed with a paid developer certificate, which is why each system asks for confirmation the
first time. Nothing needs to be installed besides the app itself.

On a Mac, the first connection may ask to allow Script Runner to find devices on the local network. Allow it, or
the app cannot reach the Pi.

## Use

1. Choose **SSH** or **HTTP**, then enter the Pi's IP address. The port fills in automatically (22 for SSH, 8000
   for HTTP); change it only if your Pi uses a different one.
2. For SSH, enter the user name (normally `pi`) and the account's password. For HTTP, enter the runner's PIN.
3. **Add files…** (or drag files onto the box on the desktop): the `.py` file plus any sound or image files it uses.
   If you add more than one `.py` file, choose which one runs in the **Run:** list.
4. Press **Run**. Type input for `input()` in the box at the bottom and press Enter.
5. **Stop** (or the Esc key on the desktop) sends Ctrl+C to the script, so its `finally:` block runs. If the script
   does not stop within 3 seconds it is terminated, and then forced to quit if needed.

Each run is stopped automatically after 300 seconds. If a script cannot be confirmed stopped after the last
step, the app stops waiting for it and says so, rather than staying in "Stopping".

### Commands on the Pi (SSH mode)

In SSH mode, a command box under the Run controls runs any command on the Pi and shows its output, for example
`ps aux | grep python3`. Each command runs on its own short connection, so it works even while a script is
running or its connection is stuck. Commands that wait for input do not work there, and the app stops waiting for
a command after 30 seconds.

**Force stop all Python** sends Ctrl+C to every Python program the account started on the Pi (including scripts
started from the app or with `python3 -`), waits 2 seconds so `finally:` blocks can stop motors, then force-quits
whatever is left, and lists anything still running. It does not stop the runner server. Use it when a script or a
previous session is stuck.

### Speech, sound, and images

Scripts use the same `runner_io` functions as with the browser page ([runner-io.md](runner-io.md)). In SSH mode the
app copies `runner_io.py` next to the script on every run, so nothing needs to be installed on the Pi.

| Function | App behavior |
| :--- | :--- |
| `say(text)` | Spoken with the system's text-to-speech (Windows speech, macOS `say`, Android text-to-speech) |
| `play(file)` | Played with the system's player. Windows and macOS play `.wav` and `.mp3`; Android also plays `.ogg`. |
| `show(file)` | Shown beside the output. The app shows `.png`, `.jpg`, `.gif`, and `.bmp`; `.svg` works only in the browser page. |

### Passwords and PINs

- **Remember (secure storage)** saves the password or PIN in the operating system's secure store: Windows
  Credential Manager, the macOS Keychain, or (on Android) encrypted with a key held in the Android Keystore.
  It is never written to a file by the app, and it is off unless you check the box. Unchecking it deletes the
  saved copy.
- The app's settings file (`settings.json` in the app's data folder) holds only the connection type, IP address,
  ports, user name, speech options, and the SSH key fingerprints described below.

### SSH host keys

The first time the app connects to a Pi over SSH, it saves that Pi's SSH identity (its host key fingerprint). If a
later connection to the same address presents a different identity, the app refuses to connect and says so. That
is expected after the Pi's system is reinstalled; otherwise another computer may be using that address. To trust
the new identity, press **Forget saved SSH key** and connect again.

## The Sphero RVR firmware warning

When the Sphero RVR SDK thinks the robot's firmware is out of date, it prints "A NEWER VERSION OF FIRMWARE IS
AVAILABLE" and waits at "Press any key to continue..." before the script can run. In the app, type anything in the
input box and press Enter to continue.

To stop the warning:

- **Update the robot's firmware** with the Sphero EDU app. This removes the warning until Sphero releases newer
  firmware.
- **Or turn the check off.** The SDK checks only when more than 7 days have passed since the date stored in a file
  named `.fw` in the folder that holds the `sphero_sdk` package. Storing a date in late 2037 turns the check off
  until then (a later date can crash the SDK on 32-bit Raspberry Pi OS). For an SDK installed with pip for the
  user, run this on the Pi (for example in the app's command box):

  ```bash
  echo 2145830400 > "$(python3 -c 'import os, sphero_sdk; print(os.path.dirname(os.path.dirname(sphero_sdk.__file__)))')/.fw"
  ```

  This also removes the few seconds the check adds to every script's start, and its use of the internet.

## SSH mode details

For people maintaining the Pis:

- Files are copied with SFTP to `~/script_runner_ssh/run` (replaced on every run), with two helper files:
  `runner_io.py` and `_script_runner_shim.py`.
- The script runs as `python3 -u` in a terminal session (so `input()` works) under `flock` on
  `/tmp/pi-script-runner.lock`. The runner server takes the same lock, so a script started from the app and
  one started from the browser page never run at the same time.
- Stop sends `SIGINT` to the script's Python process only, then `SIGTERM`, then `SIGKILL`, 3 seconds apart.
- If the connection drops while a script runs (Wi-Fi loss, laptop closed), the script receives `SIGHUP`, which
  the shim turns into `KeyboardInterrupt`, so the `finally:` block still runs.

## Building and releasing

The app is written in C# with .NET 10 and Avalonia, in the `app` folder:

```text
app/
  src/ScriptRunner.Core      Connections (SSH and HTTP) and the output parser; no user interface
  src/ScriptRunner.App       The screen, shared by desktop and Android
  src/ScriptRunner.Desktop   Windows, macOS, and Linux app
  src/ScriptRunner.Android   Android app
  tests/ScriptRunner.Tests   Tests (xUnit v3)
```

Every push to GitHub runs all the tests. Every push to `main` also builds the Windows, Mac, and Android files and
publishes them as a release named for the build date and time. Nothing needs to be built on your own computer.

To build or test locally, install the .NET 10 SDK, then from the `app` folder:

```bash
dotnet test --project tests/ScriptRunner.Tests
dotnet run --project src/ScriptRunner.Desktop
```

The SSH tests run only when these environment variables name an SSH account to test against (any Linux computer
or Pi with `python3`, `flock`, and `pkill`):

```text
SCRIPT_RUNNER_TEST_SSH_HOST, SCRIPT_RUNNER_TEST_SSH_PORT, SCRIPT_RUNNER_TEST_SSH_USER, SCRIPT_RUNNER_TEST_SSH_PASSWORD
```

The password is read only from the environment; never put it in a file in the repository.

The build treats every warning as an error and fails if a package is deprecated or has a known vulnerability. The
GitHub build also reports when newer package versions are available.

### Android signing (one-time setup)

Android installs a new version over an older one only when both are signed with the same key. The key is a
keystore file that must never be committed. GitHub needs it and its password as two repository secrets:

1. On GitHub, open the repository's **Settings > Secrets and variables > Actions**.
2. **New repository secret**: name `SCRIPTRUNNER_KEYSTORE_BASE64`, value the long text from the
   `SCRIPTRUNNER_KEYSTORE_BASE64` section of `android-signing-secrets.txt`.
3. **New repository secret**: name `SCRIPTRUNNER_KEYSTORE_PASSWORD`, value the password from the same file.
4. Keep `scriptrunner-release.keystore` and `android-signing-secrets.txt` somewhere safe outside the repository.
   If the keystore is lost, a new one works, but users must uninstall the old Android app before installing a new
   one.

Until the secrets are set, the Android job fails with a message saying so, and the Windows and Mac files are still
released. The Android file is added to each release a few minutes after the release appears.

To create a new keystore (Java's `keytool`):

```bash
keytool -genkeypair -v -keystore scriptrunner-release.keystore -alias scriptrunner -keyalg RSA -keysize 4096 -validity 10000
```
