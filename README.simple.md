# Azeroth Platform — simple guide (Express Setup)

This page is for people who mainly want to **play** Wrath of the Lich King on their own PC, with
bots for company, and who do not want to learn Docker, Linux, or server admin.

If that is you, use **Express Setup**. It runs everything on **this computer**. Friends on other PCs
cannot connect to an Express server outside of your local network. That is intentional.

> **Not for a public realm.** This project is for solo play, friends, and family. It is not a
> production hosting product. See the warning at the top of the [main README](./README.md).

Want more options later (remote servers, custom cores, extra settings)? Use the
[Technical README](./README.technical.md).

---

## What you will get

A private 3.3.5a (Wrath) server on your PC, with:

- **Individual Progression** (content unlocks as you play, instead of the whole game being open)
- **Playerbots** (AI characters in the world)
- Auction House bots
- The game client downloaded for you after the first build

The first time takes a while. Plan on **an hour or more**, mostly waiting. Your PC needs to stay on
and stay connected to the internet.

---

## What you need

- **Windows 10 or 11** (this is the easy path). Linux and macOS work too, but you will type a few
  commands — see the end of this page.
- About **16 GB of RAM**. 8 GB can work, but the first compile often fails or thrashes.
- At least **40 GB free disk** (the compile and the client are large).
- A reasonably fast **internet** connection.
- Ports **80** and **443** free on this PC (nothing else should already be a “website” on this
  machine).

You do **not** need to install Visual Studio, .NET, or Node yourself.

---

## 1. Get the project folder

1. Open [github.com/Fero-Fero/AzerothPlatform](https://github.com/Fero-Fero/AzerothPlatform).
2. Click the green **Code** button.
3. Click **Download ZIP**.
4. Unzip it somewhere easy to find, for example your Desktop.
5. Open the unzipped folder, then open **setup and commands**, until you see `1_install-platform.bat`.

A ZIP download is enough. The later **update** script can attach this folder to GitHub even if Git
for Windows is not installed.

---

## 2. Install and start the platform (Windows)

Helpers live in the **setup and commands** folder.

1. Make sure Docker Desktop is **not** already half-installed and stuck. If you have never used
   Docker, you can ignore this.
2. Double-click **`1_install-platform.bat`**.
3. If Windows asks “Do you want to allow this app…?”, click **Yes**.
4. The first run may:
   - turn on **WSL 2** (a Windows feature Docker needs)
   - ask you to **restart the PC**
   - install **Docker Desktop**
   - need **virtualization** turned on (Docker cannot do this for you if it is off in the BIOS)
5. If virtualization is off: open Task Manager (`Ctrl`+`Shift`+`Esc`) → **Performance** → **CPU**. If
   **Virtualization** says Disabled, turn it on in the BIOS/UEFI, then reboot and run
   `1_install-platform.bat` again. Microsoft’s walkthrough (with per-brand BIOS steps):
   [Enable virtualization on Windows](https://support.microsoft.com/windows/enable-virtualization-on-windows-c5578302-6e43-4b4b-a449-8ced115f58e1)
6. If it asked you to restart: reboot, open the same folder, and double-click
   `1_install-platform.bat` again.
7. The first build often takes **10–20 minutes**. Let the black window finish.

When it says the platform is up, you are ready to open the dashboard.

**If Docker was already installed:** the installer will not rebuild for you. Open Docker Desktop,
wait until the whale icon is idle, then double-click **`restart-platform.bat`** in **setup and commands**.

---

## 3. Open the dashboard and sign in

1. Double-click **`open-manager.bat`** in **setup and commands**, or in your browser go to **https://localhost/admin**
2. You will almost certainly see a scary page: **Your connection is not private** (Chrome) or
   similar. That is expected. The site uses a homemade certificate.
   - Chrome: **Advanced** → **Proceed to localhost**
   - Edge: **Advanced** → **Continue to localhost**
   - Firefox: **Advanced** → **Accept the Risk and Continue**
3. Sign in with the admin password.

The default password from the sample config is:

```text
password
```

That is only for a private PC. If this computer is shared, open the `.env` file in the project
folder with Notepad, change `ADMIN_PASSWORD=` to something only you know, save, then run
`restart-platform.bat`.

If `https://localhost/admin` will not load, try **http://127.0.0.1:8080/admin**.

---

## 4. Create an Express server

1. Click **Create Stack**.
2. **Deployment:** leave **Local** selected. Do **not** pick External VPC.
3. **Server:** type a name (for example `my-realm`) and choose **Express Setup**.
4. **Modules:** leave the defaults unless you know you want extras. **AI Bot Chatting** is a
   single-select group: **Bot Buddy**, **Chat**, or **LLM Chatter** — pick one. Choosing any of them
   turns **Playerbots** on automatically. The stack starts its own Ollama container and downloads the
   model on first start (a multi-gigabyte download). You do not need Ollama installed on this PC.
5. **Addons:** optional. You can skip extra addons on the first run.
6. **Bots:** how many random bots should log in when setup finishes.
   - Try **20–50** on a first run.
   - Use **0** if you only want to log in yourself at first.
   - High numbers (hundreds) need a strong PC.

Then start the create/build. The **first compile often takes 15–30 minutes**. Watch the progress on
the stack page. Leave the PC alone.

When the build finishes, **Express Setup keeps going by itself**: it downloads the client, applies
the first progression patch, then starts the server with bots. Wait until it says Express Setup
finished. Do not click random extra “validate patches” steps for Express — it does that for you.

---

## 5. Make a game account and play

1. Open your stack.
2. If the Overview page asks you to **initialize the SOAP account**, click that once. Account
   creation needs it.
3. Open the **Accounts** tab → **Create Account**. Pick a username and password. Set **GM level 3**
   if you want in-game admin commands.
4. Open the **Client** tab and download the client / launcher the platform prepared.
5. Start the game from that download. Log in with the account you just created.

Play on **this same PC**. Express is bound to this machine only (`127.0.0.1`).

---

## Everyday use

| You want to… | Do this |
| --- | --- |
| Play again tomorrow | Open **Docker Desktop**, wait until it is idle, double-click **`setup and commands/open-manager.bat`**, open your stack, click **Start** if it is stopped |
| Update the platform | Double-click **`setup and commands/update-platform.bat`**, then **`setup and commands/restart-platform.bat`** |
| Rebuild after a change | Double-click **`setup and commands/restart-platform.bat`** |
| Stop using the PC’s extra CPU | In the dashboard, **Stop** the stack. You can quit Docker Desktop after that |

Keep **Docker Desktop running** while you play. If Docker is closed, the server is closed.

---

## If something goes wrong

**The installer asked for a reboot.**  
That is normal for first-time WSL / Docker. Reboot, then run `1_install-platform.bat` again.

**Docker says virtualization is not enabled / “virtualization support not detected”.**  
A script cannot turn this on. Check Task Manager (`Ctrl`+`Shift`+`Esc`) → **Performance** → **CPU**.
If **Virtualization** is Disabled, enable it in the BIOS/UEFI, save, and reboot. Follow Microsoft’s
guide (pick your PC brand):
[Enable virtualization on Windows](https://support.microsoft.com/windows/enable-virtualization-on-windows-c5578302-6e43-4b4b-a449-8ced115f58e1)

**“Your connection is not private.”**  
Expected. Use Advanced → proceed/continue to localhost.

**Wrong password.**  
Open `.env` in the project folder and look at `ADMIN_PASSWORD`. If you changed it, run
`restart-platform.bat` after saving.

**Docker whale icon keeps animating / engine not ready.**  
Open Docker Desktop from the Start menu and wait until it is idle. Then run `restart-platform.bat`.

**Build failed, disk or memory.**  
Free more disk (tens of gigabytes). Close other heavy programs. 16 GB RAM is strongly recommended
for the first compile.

**Express Setup failed.**  
Read the red message on the stack page. Common causes: the PC slept, Docker stopped, or the
internet dropped during client download. Start Docker, open the stack, and retry from the message
on that page if a retry is offered.

**I picked External VPC by mistake.**  
Go back and choose **Local**, then **Express Setup**. Express does not run on a cloud VM.

**I want friends to connect from other PCs.**  
Express cannot do that. Use a non-Express server type and the [Technical README](./README.technical.md).
That is a different, more involved setup.

---

## Linux or macOS (short version)

Express Setup still works. The Windows `.bat` helpers do not.

```bash
git clone https://github.com/Fero-Fero/AzerothPlatform.git
cd AzerothPlatform
cp .env.example .env
docker compose up -d --build
```

Then open **https://localhost/admin**, accept the certificate warning, sign in with
`ADMIN_PASSWORD` from `.env`, and follow [Create an Express server](#4-create-an-express-server)
above. You need Docker already installed: [docs.docker.com/get-docker](https://docs.docker.com/get-docker/).

---

## Other documents

| Document | Who it is for |
| --- | --- |
| **[README.md](./README.md)** | Short overview and disclaimer |
| **[README.technical.md](./README.technical.md)** | Full install, remote/cloud servers, configuration, troubleshooting |
| **[DOCKER.md](./DOCKER.md)** | How the containers are laid out |
