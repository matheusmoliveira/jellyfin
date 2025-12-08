#!/usr/bin/env bash

# Script de correção rápida para instalar apenas a interface web do Play TV
# Use este script se a interface web não foi instalada corretamente

# Não usar set -e aqui porque npm pode retornar códigos de erro mesmo em sucesso
# Vamos verificar explicitamente os resultados

# Configurações do Play TV
GITHUB_USER="matheusmoliveira"
WEB_REPO="jellyfin-web"
GITHUB_BRANCH="dev"
WEB_INTERFACE_DIR="/usr/share/jellyfin/web"

# Check that we're root
if [[ $(whoami) != "root" ]]; then
    echo "ERROR: This script must be run as 'root' or with 'sudo'."
    exit 1
fi

echo "=========================================="
echo "  Play TV - Correção da Interface Web"
echo "=========================================="
echo

# Stop Jellyfin service
echo "> Stopping Jellyfin service."
systemctl stop jellyfin.service 2>/dev/null || true
echo

# Create temporary directory
TMP_DIR=$(mktemp -d)
cd "${TMP_DIR}"

# Clone web repository
echo "> Cloning Play TV web interface from GitHub."
if ! git clone --depth 1 --branch "${GITHUB_BRANCH}" "https://github.com/${GITHUB_USER}/${WEB_REPO}.git" jellyfin-web; then
    echo "ERROR: Failed to clone web repository from GitHub."
    exit 1
fi

cd jellyfin-web

# Install dependencies
echo "> Installing web build dependencies."
if ! npm install; then
    echo "ERROR: Failed to install npm dependencies."
    exit 1
fi

# Build web interface
echo "> Building Play TV web interface."
echo "  This may take several minutes..."
BUILD_LOG="/tmp/jellyfin-build.log"

# Run build and capture output
npm run build:production 2>&1 | tee "${BUILD_LOG}" || BUILD_EXIT_CODE=$?

# Check if dist directory was created (this is the real test of success)
if [[ ! -d "dist" ]]; then
    echo
    echo "ERROR: Build directory 'dist' not found after build."
    echo "Build exit code: ${BUILD_EXIT_CODE:-0}"
    echo
    echo "Last 100 lines of build output:"
    tail -n 100 "${BUILD_LOG}"
    echo
    echo "Full build log saved to: ${BUILD_LOG}"
    exit 1
fi

# Check if dist has files
if [[ -z "$(ls -A dist 2>/dev/null)" ]]; then
    echo
    echo "ERROR: Build directory 'dist' is empty."
    echo "Build exit code: ${BUILD_EXIT_CODE:-0}"
    echo
    echo "Last 100 lines of build output:"
    tail -n 100 "${BUILD_LOG}"
    exit 1
fi

# If we got here, build was successful (even if npm returned non-zero)
echo "> Build completed successfully (warnings are normal)."

# Verify dist directory exists
if [[ ! -d "dist" ]]; then
    echo "ERROR: Build directory 'dist' not found after build."
    exit 1
fi

# Check if dist has files
if [[ -z "$(ls -A dist)" ]]; then
    echo "ERROR: Build directory 'dist' is empty."
    exit 1
fi

echo "> Build completed successfully. Found $(find dist -type f | wc -l) files in dist directory."
echo

# Backup existing web interface if it exists
if [[ -d "${WEB_INTERFACE_DIR}" ]]; then
    echo "> Backing up existing web interface."
    BACKUP_DIR="${WEB_INTERFACE_DIR}.backup.$(date +%Y%m%d_%H%M%S)"
    mv "${WEB_INTERFACE_DIR}" "${BACKUP_DIR}"
    echo "  Backup created at: ${BACKUP_DIR}"
fi

# Create web directory
echo "> Creating web interface directory: ${WEB_INTERFACE_DIR}"
mkdir -p "${WEB_INTERFACE_DIR}"

# Copy files
echo "> Copying web interface files..."
if ! cp -r dist/* "${WEB_INTERFACE_DIR}/"; then
    echo "ERROR: Failed to copy files to ${WEB_INTERFACE_DIR}"
    exit 1
fi

# Set permissions
echo "> Setting permissions..."
chown -R jellyfin:jellyfin "${WEB_INTERFACE_DIR}"
chmod -R 755 "${WEB_INTERFACE_DIR}"

# Verify installation
echo "> Verifying installation..."
FILE_COUNT=$(find "${WEB_INTERFACE_DIR}" -type f | wc -l)
if [[ ${FILE_COUNT} -eq 0 ]]; then
    echo "ERROR: No files found in ${WEB_INTERFACE_DIR}"
    exit 1
fi

echo "  Found ${FILE_COUNT} files in ${WEB_INTERFACE_DIR}"
echo

# Clean up
cd /
rm -rf "${TMP_DIR}"

# Start Jellyfin service
echo "> Starting Jellyfin service."
systemctl start jellyfin.service

# Wait a bit
sleep 3

# Check status
echo
echo "> Checking service status..."
systemctl status jellyfin.service --no-pager -l || true

echo
echo "=========================================="
echo "  Interface Web Corrigida!"
echo "=========================================="
echo
echo "The web interface has been installed to: ${WEB_INTERFACE_DIR}"
echo "Jellyfin service has been started."
echo

exit 0

