#!/usr/bin/env bash

# Script para atualizar Play TV Server e Web Client no servidor rodando

set -euo pipefail

# Configurações do Play TV
GITHUB_USER="matheusmoliveira"
SERVER_REPO="jellyfin"
WEB_REPO="jellyfin-web"
GITHUB_BRANCH="dev"
WEB_INTERFACE_DIR="/usr/share/jellyfin/web"
SERVER_INSTALL_DIR="/usr/lib/jellyfin"
KEEP_TMP="${KEEP_TMP:-0}" # 1 = não apaga o diretório temporário (útil para debug)

# Runtime deps
require_cmd() {
    command -v "$1" >/dev/null 2>&1 || {
        echo "ERROR: Missing dependency '$1' in PATH."
        exit 1
    }
}

# Check that we're root; if not, fail out
if [[ $(whoami) != "root" ]]; then
    echo "ERROR: This script must be run as 'root' or with 'sudo' to function."
    echo "Try using this command instead: sudo bash update-playtv.sh"
    exit 1
fi

require_cmd git
require_cmd dotnet
require_cmd rsync
require_cmd npm

cleanup() {
    if [[ "${KEEP_TMP}" == "1" ]]; then
        echo "INFO: KEEP_TMP=1, mantendo TMP_DIR em: ${TMP_DIR:-<unset>}"
        return
    fi
    if [[ -n "${TMP_DIR:-}" && -d "${TMP_DIR:-}" ]]; then
        rm -rf "${TMP_DIR}" || true
    fi
}
trap cleanup EXIT

echo "=========================================="
echo "  Atualizando Play TV"
echo "=========================================="
echo

# Stop Jellyfin service
echo "> Parando serviço Jellyfin."
systemctl stop jellyfin.service || echo "WARNING: Failed to stop Jellyfin service. Continuing anyway..."
echo

# Create temporary directory for building
TMP_DIR=$(mktemp -d)
cd "${TMP_DIR}"

# Clone and build custom server
echo "> Clonando Play TV Server do GitHub (branch: ${GITHUB_BRANCH})."
git clone --depth 1 --branch "${GITHUB_BRANCH}" "https://github.com/${GITHUB_USER}/${SERVER_REPO}.git" jellyfin-server
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to clone server repository from GitHub."
    exit 1
fi

cd jellyfin-server

echo "> Compilando Play TV Server."
# Find the Jellyfin.Server project
SERVER_PROJECT=$(find . -path "*/Jellyfin.Server/Jellyfin.Server.csproj" | head -1)
if [[ -z "${SERVER_PROJECT}" ]]; then
    # Try alternative locations
    SERVER_PROJECT=$(find . -name "Jellyfin.Server.csproj" | head -1)
fi

if [[ -z "${SERVER_PROJECT}" ]]; then
    echo "ERROR: Could not find Jellyfin.Server.csproj"
    exit 1
fi

echo "> Projeto encontrado: ${SERVER_PROJECT}"
dotnet publish "${SERVER_PROJECT}" --configuration Release --output "${TMP_DIR}/server-build"
echo

# Install the server
echo "> Instalando Play TV Server."
if [[ ! -d "${TMP_DIR}/server-build" ]]; then
    echo "ERROR: Build output directory not found."
    exit 1
fi

# Backup current installation (optional)
if [[ -d "${SERVER_INSTALL_DIR}" ]]; then
    echo "> Fazendo backup da instalação atual."
    BACKUP_DIR="${SERVER_INSTALL_DIR}.backup.$(date +%Y%m%d_%H%M%S)"
    cp -r "${SERVER_INSTALL_DIR}" "${BACKUP_DIR}" 2>/dev/null || true
    echo "  Backup salvo em: ${BACKUP_DIR}"
fi

# Copy server files to installation directory
echo "> Copiando arquivos do servidor."
mkdir -p "${SERVER_INSTALL_DIR}"
rsync -a --delete "${TMP_DIR}/server-build"/ "${SERVER_INSTALL_DIR}/"
chown -R jellyfin:jellyfin "${SERVER_INSTALL_DIR}"
echo

cd "${TMP_DIR}"

# Clone and build custom web interface
echo "> Clonando Play TV Web Client do GitHub (branch: ${GITHUB_BRANCH})."
git clone --depth 1 --branch "${GITHUB_BRANCH}" "https://github.com/${GITHUB_USER}/${WEB_REPO}.git" jellyfin-web
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to clone web repository from GitHub."
    exit 1
fi

cd jellyfin-web

echo "> Instalando dependências do web client."
if [[ -f package-lock.json ]]; then
    npm ci --no-audit --no-fund
else
    npm install --no-audit --no-fund
fi

echo "> Compilando Play TV Web Client."
if [[ -z "${NODE_OPTIONS:-}" ]]; then
    # Ajusta heap do Node baseado na RAM disponível para reduzir chance de OOM durante o webpack.
    # (cap entre 1024 e 6144 MB)
    MEM_MB=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo 2>/dev/null || echo 0)
    if [[ "${MEM_MB}" -gt 0 ]]; then
        HEAP_MB=$((MEM_MB * 70 / 100))
        if [[ "${HEAP_MB}" -lt 1024 ]]; then HEAP_MB=1024; fi
        if [[ "${HEAP_MB}" -gt 6144 ]]; then HEAP_MB=6144; fi
        export NODE_OPTIONS="--max-old-space-size=${HEAP_MB}"
        echo "INFO: NODE_OPTIONS não definido; usando ${NODE_OPTIONS} (MemTotal=${MEM_MB}MB)"
    else
        export NODE_OPTIONS="--max-old-space-size=2048"
        echo "INFO: NODE_OPTIONS não definido; usando ${NODE_OPTIONS}"
    fi
else
    echo "INFO: usando NODE_OPTIONS existente: ${NODE_OPTIONS}"
fi

npm run build:production

# Backup current web interface
if [[ -d "${WEB_INTERFACE_DIR}" ]]; then
    echo "> Fazendo backup da interface web atual."
    WEB_BACKUP_DIR="${WEB_INTERFACE_DIR}.backup.$(date +%Y%m%d_%H%M%S)"
    cp -r "${WEB_INTERFACE_DIR}" "${WEB_BACKUP_DIR}" 2>/dev/null || true
    echo "  Backup salvo em: ${WEB_BACKUP_DIR}"
fi

# Install web interface
echo "> Instalando Play TV Web Client."
mkdir -p "${WEB_INTERFACE_DIR}"
if [[ ! -d dist ]]; then
    echo "ERROR: Build output directory 'dist' not found."
    exit 1
fi
rsync -a --delete dist/ "${WEB_INTERFACE_DIR}/"
chown -R jellyfin:jellyfin "${WEB_INTERFACE_DIR}"
echo

# Start Jellyfin service
echo "> Reiniciando serviço Jellyfin."
systemctl start jellyfin.service

# Wait for Jellyfin to start up
echo "> Aguardando 15 segundos para o Jellyfin iniciar completamente."
sleep 15
echo

# Output the result of systemctl status
echo "-------------------------------------------------------------------------------"
export SYSTEMD_PAGER=
systemctl status jellyfin.service || service jellyfin status
echo "-------------------------------------------------------------------------------"
echo

echo "=========================================="
echo "  Atualização Concluída!"
echo "=========================================="
echo
echo "✅ Play TV foi atualizado com sucesso!"
echo
echo "O servidor foi reiniciado e está rodando com as novas versões."
echo

exit 0

