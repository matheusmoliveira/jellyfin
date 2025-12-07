#!/usr/bin/env bash

# Script para atualizar Play TV Server e Web Client no servidor rodando

# Configurações do Play TV
GITHUB_USER="matheusmoliveira"
SERVER_REPO="jellyfin"
WEB_REPO="jellyfin-web"
GITHUB_BRANCH="dev"
WEB_INTERFACE_DIR="/usr/share/jellyfin/web"
SERVER_INSTALL_DIR="/usr/lib/jellyfin"

# Check that we're root; if not, fail out
if [[ $(whoami) != "root" ]]; then
    echo "ERROR: This script must be run as 'root' or with 'sudo' to function."
    echo "Try using this command instead: sudo bash update-playtv.sh"
    exit 1
fi

echo "=========================================="
echo "  Atualizando Play TV"
echo "=========================================="
echo

# Stop Jellyfin service
echo "> Parando serviço Jellyfin."
systemctl stop jellyfin.service
if [[ $? -gt 0 ]]; then
    echo "WARNING: Failed to stop Jellyfin service. Continuing anyway..."
fi
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
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to build server."
    exit 1
fi
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
cp -r "${TMP_DIR}/server-build"/* "${SERVER_INSTALL_DIR}/"
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
npm install
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to install npm dependencies."
    exit 1
fi

echo "> Compilando Play TV Web Client."
npm run build:production
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to build web interface."
    exit 1
fi

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
cp -r dist/* "${WEB_INTERFACE_DIR}/"
chown -R jellyfin:jellyfin "${WEB_INTERFACE_DIR}"
echo

# Clean up
cd /
rm -rf "${TMP_DIR}"

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

