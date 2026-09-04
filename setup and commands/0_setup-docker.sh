#!/usr/bin/env bash
# Checks this OS. On Windows, firmware virtualization must be on before Docker is installed.
# On Linux and macOS, installs Docker when it is missing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$ROOT/.." && pwd)"
cd "$ROOT"

step() { printf '\n==> %s\n' "$1"; }
ok() { printf '    %s\n' "$1"; }
warn() { printf '    %s\n' "$1"; }

os="$(uname -s 2>/dev/null || echo unknown)"

echo "Azeroth Platform Docker setup"
echo "Operating system: $os"

docker_ready() {
  command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

install_linux_docker() {
  if command -v docker >/dev/null 2>&1; then
    if docker_ready; then
      ok "Docker is already installed and the engine is running ($(command -v docker))."
      return 0
    fi
    step "Starting the Docker engine"
    if command -v systemctl >/dev/null 2>&1; then
      sudo systemctl enable --now docker
    elif command -v service >/dev/null 2>&1; then
      sudo service docker start
    fi
    if docker_ready; then
      ok "Docker engine is ready."
      return 0
    fi
    echo "Docker is installed but the engine is not running. Start it, then run this script again." >&2
    exit 1
  fi

  step "Installing Docker Engine"
  if ! command -v curl >/dev/null 2>&1; then
    echo "curl is required to download Docker." >&2
    exit 1
  fi

  tmp="$(mktemp)"
  # Official installer from get.docker.com; it picks packages for this distro.
  curl -fsSL https://get.docker.com -o "$tmp"
  sudo sh "$tmp"
  rm -f "$tmp"

  if command -v systemctl >/dev/null 2>&1; then
    sudo systemctl enable --now docker
  fi

  if id -nG "$USER" 2>/dev/null | grep -qw docker; then
    ok "User $USER is already in the docker group."
  else
    sudo usermod -aG docker "$USER"
    warn "Added $USER to the docker group. Log out and back in before using docker without sudo."
  fi

  if sudo docker info >/dev/null 2>&1; then
    ok "Docker engine is ready."
    return 0
  fi

  echo "Docker installed, but the engine did not become ready. Try: sudo systemctl start docker" >&2
  exit 1
}

install_macos_docker() {
  if docker_ready; then
    ok "Docker is already installed and the engine is running ($(command -v docker))."
    return 0
  fi

  if command -v docker >/dev/null 2>&1 || [[ -d /Applications/Docker.app ]]; then
    step "Starting Docker Desktop"
    open -a Docker
  else
    step "Installing Docker Desktop"
    if command -v brew >/dev/null 2>&1; then
      brew install --cask docker
      open -a Docker
    else
      arch="$(uname -m)"
      if [[ "$arch" == "arm64" ]]; then
        url="https://desktop.docker.com/mac/main/arm64/Docker.dmg"
      else
        url="https://desktop.docker.com/mac/main/amd64/Docker.dmg"
      fi
      dmg="$(mktemp -t DockerDesktop).dmg"
      curl -fL "$url" -o "$dmg"
      mnt="$(mktemp -d)"
      hdiutil attach "$dmg" -nobrowse -mountpoint "$mnt"
      cp -R "$mnt/Docker.app" /Applications/
      hdiutil detach "$mnt"
      rm -f "$dmg"
      rmdir "$mnt" 2>/dev/null || true
      open -a Docker
    fi
  fi

  echo "    Waiting up to 480 seconds for the engine..."
  deadline=$((SECONDS + 480))
  while (( SECONDS < deadline )); do
    if docker_ready; then
      ok "Docker engine is ready."
      return 0
    fi
    sleep 5
  done

  echo "Docker Desktop did not become ready in time. Open Docker from Applications, wait until it is idle, then run this script again." >&2
  exit 1
}

case "$os" in
  Linux)
    install_linux_docker
    ;;
  Darwin)
    install_macos_docker
    ;;
  MINGW*|MSYS*|CYGWIN*|Windows_NT)
    echo
    echo "Windows detected. Checking virtualization and Docker with PowerShell."
    exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$ROOT/0_setup-docker.ps1" "$@"
    ;;
  *)
    echo "Unsupported operating system: $os" >&2
    echo "On Windows double-click 0_setup-docker.bat in \"setup and commands\"." >&2
    exit 1
    ;;
esac

echo
echo "Docker is ready."
if [[ "$os" == Linux || "$os" == Darwin ]]; then
  echo "From the repository root ($REPO_ROOT) you can now run: docker compose up -d --build"
fi
