#!/usr/bin/env bash

shopt -s extglob

# Configurações do Play TV
GITHUB_USER="matheusmoliveira"
SERVER_REPO="jellyfin"
WEB_REPO="jellyfin-web"
GITHUB_BRANCH="dev"
WEB_INTERFACE_DIR="/usr/share/jellyfin/web"
SERVER_INSTALL_DIR="/usr/lib/jellyfin"

# Lists of supported architectures, Debian, and Ubuntu releases
SUPPORTED_ARCHITECTURES='@(amd64|armhf|arm64)'
SUPPORTED_DEBIAN_RELEASES='@(bullseye|bookworm|trixie)'
SUPPORTED_UBUNTU_RELEASES='@(focal|jammy|noble)'

GPG_KEY_URL="https://repo.jellyfin.org/jellyfin_team.gpg.key"
DOWNLOADS_URL="https://jellyfin.org/downloads/server"
CONTACT_URL="https://jellyfin.org/contact"

# Fail out if we can't find /etc/apt or /etc/os-release
if [[ ! -d /etc/apt || ! -f /etc/os-release ]]; then
    echo "ERROR: Couldn't find the '/etc/apt' directory or '/etc/os-release' manifest."
    echo "This script is for Debian-based distributions using APT only. Please consider a Docker-based or manual install instead: ${DOWNLOADS_URL}"
    exit 1
fi

# Check that we're root; if not, fail out
if [[ $(whoami) != "root" ]]; then
    echo "ERROR: This script must be run as 'root' or with 'sudo' to function."
    echo "Try using this command instead: curl https://raw.githubusercontent.com/${GITHUB_USER}/${WEB_REPO}/${GITHUB_BRANCH}/install-playtv.sh | sudo bash"
    exit 1
fi

echo "=========================================="
echo "  Play TV - Instalação Customizada"
echo "=========================================="
echo

echo "> Determining optimal repository settings."

# Get the (dpkg) architecture and base OS from /etc/os-release
ARCHITECTURE="$( dpkg --print-architecture )"
BASE_OS="$( awk -F'=' '/^ID=/{ print $NF }' /etc/os-release )"

# Validate that we're running on a supported (dpkg) architecture
# shellcheck disable=SC2254
case "${ARCHITECTURE}" in
    ${SUPPORTED_ARCHITECTURES})
        true
    ;;
    *)
        echo "ERROR: We don't support the CPU architecture '${ARCHITECTURE}' with this script."
        echo "Please consider a Docker-based or manual install instead: ${DOWNLOADS_URL}"
        exit 1
    ;;
esac

# Handle some known alternative base OS values with 1-to-1 mappings
case "${BASE_OS}" in
    raspbian)
        REPO_OS="debian"
        VERSION="$( awk -F'=' '/^VERSION_CODENAME=/{ print $NF }' /etc/os-release )"
    ;;
    neon)
        REPO_OS="ubuntu"
        VERSION="$( awk -F'=' '/^VERSION_CODENAME=/{ print $NF }' /etc/os-release )"
    ;;
    tuxedo)
        REPO_OS="ubuntu"
        VERSION="$( awk -F'=' '/^VERSION_CODENAME=/{ print $NF }' /etc/os-release )"
    ;;
    *)
        if grep -q "DEBIAN_CODENAME=" /etc/os-release &>/dev/null; then
            REPO_OS="debian"
            VERSION="$( awk -F'=' '/^DEBIAN_CODENAME=/{ print $NF }' /etc/os-release )"
        elif grep -q "UBUNTU_CODENAME=" /etc/os-release &>/dev/null; then
            REPO_OS="ubuntu"
            VERSION="$( awk -F'=' '/^UBUNTU_CODENAME=/{ print $NF }' /etc/os-release )"
        else
            REPO_OS="${BASE_OS}"
            VERSION="$( awk -F'=' '/^VERSION_CODENAME=/{ print $NF }' /etc/os-release )"
        fi
    ;;
esac

# Validate that we're running a supported release
case "${REPO_OS}" in
    debian)
        case "${VERSION}" in
            ${SUPPORTED_DEBIAN_RELEASES})
                true
            ;;
            *)
                echo "ERROR: We don't support the Debian codename '${VERSION}' with this script."
                echo "Note: We only support stable versions of Debian."
                exit 1
            ;;
        esac
    ;;
    ubuntu)
        case "${VERSION}" in
            ${SUPPORTED_UBUNTU_RELEASES})
                true
            ;;
            *)
                echo "ERROR: We don't support the Ubuntu codename '${VERSION}' with this script."
                echo "Note: We only support LTS versions of Ubuntu."
                exit 1
            ;;
        esac
    ;;
    *)
        YELLOW='\033[0;33m'
        NC='\033[0m'
        echo -e "${YELLOW}WARNING${NC}: Autodetection of base OS and version failed."
        echo -e "To continue, please enter the following as 'Repo OS' and 'Repo Release', respectively:"
        echo -e "  (1) The upstream distribution of your current distro (either 'debian' or 'ubuntu')."
        echo -e "  (2) The closest upstream release codename of your current distro ('bookworm', 'focal', etc.)."
        echo
        echo -en "Repo OS: "
        read -r REPO_OS < /dev/tty
        echo -en "Repo Release: "
        read -r VERSION < /dev/tty
    ;;
esac

echo
echo -e "Found the following details from '/etc/os-release':"
echo -e "  Real OS:            ${BASE_OS}"
echo -e "  Repository OS:      ${REPO_OS}"
echo -e "  Repository Release: ${VERSION}"
echo -e "  CPU Architecture:   ${ARCHITECTURE}"
echo -e "  Custom Server:      https://github.com/${GITHUB_USER}/${SERVER_REPO} (branch: ${GITHUB_BRANCH})"
echo -e "  Custom Web UI:      https://github.com/${GITHUB_USER}/${WEB_REPO} (branch: ${GITHUB_BRANCH})"
echo -en "If this looks correct, press <Enter> now to continue installing Play TV. "
if [[ ! "${SKIP_CONFIRM,,}" =~ ^(true|1)$ ]]; then
    read -r < /dev/tty
fi

echo

# Get the paths to curl and wget
CURL=$( which curl )
WGET=$( which wget )

# Create our array of to-be-installed packages
INSTALL_PKGS=()

# Pick our optimal fetching program
if [[ -n ${CURL} ]]; then
    FETCH="${CURL} -fsSL"
elif [[ -n ${WGET} ]]; then
    FETCH="${WGET} -O-"
else
    echo "Failed to find a suitable download program. Installing 'curl' automatically."
    INSTALL_PKGS=( ${INSTALL_PKGS[@]} curl )
    FETCH="${CURL} -fsSL"
    echo
fi

# Check for required build tools
BUILD_TOOLS=()
if ! command -v git &> /dev/null; then
    BUILD_TOOLS+=( git )
fi
if ! command -v node &> /dev/null; then
    BUILD_TOOLS+=( nodejs npm )
fi

# .NET SDK is required for building the server
if ! command -v dotnet &> /dev/null; then
    BUILD_TOOLS+=( wget )
fi

# Get the path to gpg or install it
GNUPG=$( which gpg )
if [[ -z ${GNUPG} ]]; then
    INSTALL_PKGS=( ${INSTALL_PKGS[@]} gnupg )
fi

# If we have dependencies to install, do so
ALL_PKGS=( ${INSTALL_PKGS[@]} ${BUILD_TOOLS[@]} )
if [[ ${#ALL_PKGS[@]} -gt 0 ]]; then
    echo "> Updating APT repositories."
    apt update
    echo
    echo "> Installing required dependencies."
    apt install --yes "${ALL_PKGS[@]}"
    echo
fi

# Install Node.js 20.x if not present
if ! command -v node &> /dev/null; then
    echo "> Installing Node.js 20.x"
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
    apt install --yes nodejs
    echo
elif command -v node &> /dev/null; then
    NODE_VERSION=$(node -v | cut -d'v' -f2 | cut -d'.' -f1)
    if [[ ${NODE_VERSION} -lt 20 ]]; then
        echo "> Upgrading Node.js to version 20.x"
        curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
        apt install --yes nodejs
        echo
    else
        echo "> Node.js $(node -v) is already installed."
    fi
fi

# Install .NET SDK 9.0 if not present (required for building Jellyfin Server)
# Try 9.0 first, fallback to 8.0 if not available
if ! command -v dotnet &> /dev/null; then
    echo "> Installing .NET SDK"
    # Install prerequisites
    apt install --yes software-properties-common wget
    # Add Microsoft package repository
    # Try to download the config package, handle errors gracefully
    if wget https://packages.microsoft.com/config/${REPO_OS}/${VERSION}/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb 2>/dev/null; then
        dpkg -i /tmp/packages-microsoft-prod.deb
        rm /tmp/packages-microsoft-prod.deb
        apt update
        # Try to install .NET SDK 9.0, fallback to 8.0 if not available
        if apt install --yes dotnet-sdk-9.0 2>/dev/null; then
            echo "> .NET SDK 9.0 installed successfully."
        elif apt install --yes dotnet-sdk-8.0 2>/dev/null; then
            echo "> .NET SDK 8.0 installed (9.0 not available for this OS version)."
        else
            echo "ERROR: Failed to install .NET SDK. Please install manually."
            exit 1
        fi
    else
        # If the config package doesn't exist for this OS version, try using the generic Ubuntu/Debian config
        echo "> Config package not found for ${REPO_OS}/${VERSION}, trying generic approach..."
        if [[ "${REPO_OS}" == "ubuntu" ]]; then
            # Try jammy (22.04) config as fallback for newer Ubuntu versions
            wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb 2>/dev/null || \
            wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
        elif [[ "${REPO_OS}" == "debian" ]]; then
            # Try bookworm (12) config as fallback for newer Debian versions
            wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb 2>/dev/null || \
            wget https://packages.microsoft.com/config/debian/11/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
        fi
        if [[ -f /tmp/packages-microsoft-prod.deb ]]; then
            dpkg -i /tmp/packages-microsoft-prod.deb
            rm /tmp/packages-microsoft-prod.deb
            apt update
            # Try to install .NET SDK 9.0, fallback to 8.0 if not available
            if apt install --yes dotnet-sdk-9.0 2>/dev/null; then
                echo "> .NET SDK 9.0 installed successfully."
            elif apt install --yes dotnet-sdk-8.0 2>/dev/null; then
                echo "> .NET SDK 8.0 installed (9.0 not available for this OS version)."
            else
                echo "ERROR: Failed to install .NET SDK. Please install manually."
                exit 1
            fi
        else
            echo "ERROR: Could not download Microsoft package repository configuration."
            echo "Please install .NET SDK manually: https://dotnet.microsoft.com/download"
            exit 1
        fi
    fi
    echo
elif command -v dotnet &> /dev/null; then
    DOTNET_VERSION=$(dotnet --version | cut -d'.' -f1)
    if [[ ${DOTNET_VERSION} -lt 8 ]]; then
        echo "> Upgrading .NET SDK to version 8.0 or 9.0"
        # Install prerequisites if needed
        apt install --yes software-properties-common wget 2>/dev/null || true
        # Try to add Microsoft repository if not already added
        if [[ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]]; then
            if wget https://packages.microsoft.com/config/${REPO_OS}/${VERSION}/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb 2>/dev/null; then
                dpkg -i /tmp/packages-microsoft-prod.deb
                rm /tmp/packages-microsoft-prod.deb
                apt update
            fi
        fi
        # Try to upgrade to 9.0, fallback to 8.0
        if apt install --yes dotnet-sdk-9.0 2>/dev/null; then
            echo "> .NET SDK upgraded to 9.0."
        elif apt install --yes dotnet-sdk-8.0 2>/dev/null; then
            echo "> .NET SDK upgraded to 8.0 (9.0 not available)."
        else
            echo "WARNING: Could not upgrade .NET SDK. Current version: $(dotnet --version)"
        fi
        echo
    else
        echo "> .NET SDK $(dotnet --version) is already installed."
    fi
fi

# If the keyring directory is absent, create it
if [[ ! -d /etc/apt/keyrings ]]; then
    echo "> Creating APT keyring directory."
    mkdir -p /etc/apt/keyrings
    echo
fi

# Download our repository signing key and install it
echo "> Fetching repository signing key."
$FETCH ${GPG_KEY_URL} | gpg --dearmor --yes --output /etc/apt/keyrings/jellyfin.gpg
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to install key. Use ${CONTACT_URL} to find us for troubleshooting."
    exit 1
fi
chmod 644 /etc/apt/keyrings/jellyfin.gpg
echo

# Check for and remove the obsoleted jellyfin.list configuration if present
if [[ -f /etc/apt/sources.list.d/jellyfin.list ]]; then
    echo "> Found old-style '/etc/apt/sources.list.d/jellyfin.list' configuration; removing it."
    rm -f /etc/apt/sources.list.d/jellyfin.list
    echo
fi

# Install the Deb822 format jellyfin.sources entry
echo "> Installing Jellyfin repository into APT."
cat <<EOF | tee /etc/apt/sources.list.d/jellyfin.sources
Types: deb
URIs: https://repo.jellyfin.org/${REPO_OS}
Suites: ${VERSION}
Components: main
Architectures: ${ARCHITECTURE}
Signed-By: /etc/apt/keyrings/jellyfin.gpg
EOF
echo

# Update the apt repositories
echo "> Updating APT repositories."
apt update
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to update APT repositories."
    exit 1
fi
echo

# Install only jellyfin-ffmpeg from official repository (we'll build server and web from source)
echo "> Installing Jellyfin FFmpeg from official repository."
# Try jellyfin-ffmpeg7 first (newest), then jellyfin-ffmpeg6, then jellyfin-ffmpeg
FFMPEG_PKG=""
if apt-cache show jellyfin-ffmpeg7 &>/dev/null; then
    FFMPEG_PKG="jellyfin-ffmpeg7"
elif apt-cache show jellyfin-ffmpeg6 &>/dev/null; then
    FFMPEG_PKG="jellyfin-ffmpeg6"
elif apt-cache show jellyfin-ffmpeg &>/dev/null; then
    FFMPEG_PKG="jellyfin-ffmpeg"
else
    echo "ERROR: No jellyfin-ffmpeg package found in repository."
    exit 1
fi

if apt install --yes "${FFMPEG_PKG}"; then
    echo "> ${FFMPEG_PKG} installed successfully."
else
    echo "ERROR: Failed to install ${FFMPEG_PKG}."
    exit 1
fi
echo

# Create jellyfin user and directories if they don't exist
if ! id "jellyfin" &>/dev/null; then
    echo "> Creating jellyfin user."
    useradd -r -s /bin/false -d /var/lib/jellyfin jellyfin
fi

# Create necessary directories
echo "> Creating Jellyfin directories."
mkdir -p /var/lib/jellyfin
mkdir -p /var/cache/jellyfin
mkdir -p /var/log/jellyfin
mkdir -p /etc/jellyfin
mkdir -p "${SERVER_INSTALL_DIR}"
chown -R jellyfin:jellyfin /var/lib/jellyfin
chown -R jellyfin:jellyfin /var/cache/jellyfin
chown -R jellyfin:jellyfin /var/log/jellyfin
chown -R jellyfin:adm /etc/jellyfin
echo

# Create temporary directory for building
TMP_DIR=$(mktemp -d)
cd "${TMP_DIR}"

# Clone and build custom server
echo "> Cloning Play TV Server from GitHub."
git clone --depth 1 --branch "${GITHUB_BRANCH}" "https://github.com/${GITHUB_USER}/${SERVER_REPO}.git" jellyfin-server
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to clone server repository from GitHub."
    exit 1
fi

cd jellyfin-server

echo "> Building Play TV Server."
# Build the server (adjust build command based on your server structure)
# Common Jellyfin Server build commands:
if [[ -f "build.sh" ]]; then
    bash build.sh
    SERVER_BUILD_DIR="bin/Release"
elif [[ -f "Jellyfin.sln" ]]; then
    # Find the Jellyfin.Server project
    SERVER_PROJECT=$(find . -path "*/Jellyfin.Server/Jellyfin.Server.csproj" | head -1)
    if [[ -n "${SERVER_PROJECT}" ]]; then
        echo "> Found Jellyfin.Server project: ${SERVER_PROJECT}"
        dotnet publish "${SERVER_PROJECT}" --configuration Release --output "${TMP_DIR}/server-build"
    else
        echo "ERROR: Could not find Jellyfin.Server.csproj in the solution."
        exit 1
    fi
else
    # Try to find any .sln file and the server project
    SOLUTION_FILE=$(find . -maxdepth 1 -name "*.sln" | head -1)
    if [[ -n "${SOLUTION_FILE}" ]]; then
        echo "> Found solution file: ${SOLUTION_FILE}"
        SERVER_PROJECT=$(find . -path "*/Jellyfin.Server/Jellyfin.Server.csproj" -o -name "Jellyfin.Server.csproj" | head -1)
        if [[ -n "${SERVER_PROJECT}" ]]; then
            echo "> Found Jellyfin.Server project: ${SERVER_PROJECT}"
            dotnet publish "${SERVER_PROJECT}" --configuration Release --output "${TMP_DIR}/server-build"
        else
            echo "ERROR: Could not find Jellyfin.Server.csproj in the solution."
            exit 1
        fi
    else
        # Try to find the main project file directly
        SERVER_PROJECT=$(find . -name "Jellyfin.Server.csproj" | head -1)
        if [[ -n "${SERVER_PROJECT}" ]]; then
            echo "> Found Jellyfin.Server project: ${SERVER_PROJECT}"
            dotnet publish "${SERVER_PROJECT}" --configuration Release --output "${TMP_DIR}/server-build"
        else
            echo "ERROR: Could not find Jellyfin Server project file or solution file."
            echo "Please ensure the server repository contains a valid .NET project."
            exit 1
        fi
    fi
fi

if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to build server."
    exit 1
fi

# Install the server
echo "> Installing Play TV Server."
if [[ -d "${TMP_DIR}/server-build" ]]; then
    SERVER_BUILD_DIR="${TMP_DIR}/server-build"
elif [[ -d "bin/Release" ]]; then
    SERVER_BUILD_DIR="bin/Release"
else
    SERVER_BUILD_DIR=$(find . -type d -name "publish" | head -1)
fi

if [[ -z "${SERVER_BUILD_DIR}" || ! -d "${SERVER_BUILD_DIR}" ]]; then
    echo "ERROR: Could not find server build output directory."
    exit 1
fi

# Copy server files to installation directory
cp -r "${SERVER_BUILD_DIR}"/* "${SERVER_INSTALL_DIR}/"
chown -R jellyfin:jellyfin "${SERVER_INSTALL_DIR}"

# Find the Jellyfin executable
JELLYFIN_EXE=""
if [[ -f "${SERVER_INSTALL_DIR}/jellyfin" ]]; then
    JELLYFIN_EXE="${SERVER_INSTALL_DIR}/jellyfin"
elif [[ -f "${SERVER_INSTALL_DIR}/Jellyfin.Server" ]]; then
    JELLYFIN_EXE="${SERVER_INSTALL_DIR}/Jellyfin.Server"
else
    JELLYFIN_EXE=$(find "${SERVER_INSTALL_DIR}" -type f -name "jellyfin" -o -name "Jellyfin.Server" | head -1)
fi

if [[ -z "${JELLYFIN_EXE}" ]]; then
    echo "WARNING: Could not find Jellyfin executable, using default path."
    JELLYFIN_EXE="/usr/lib/jellyfin/jellyfin"
fi

# Find FFmpeg path (check multiple possible locations for different versions)
FFMPEG_PATH=""
if [[ -f "/usr/lib/jellyfin-ffmpeg/ffmpeg" ]]; then
    FFMPEG_PATH="/usr/lib/jellyfin-ffmpeg/ffmpeg"
elif [[ -f "/usr/lib/jellyfin-ffmpeg7/ffmpeg" ]]; then
    FFMPEG_PATH="/usr/lib/jellyfin-ffmpeg7/ffmpeg"
elif [[ -f "/usr/lib/jellyfin-ffmpeg6/ffmpeg" ]]; then
    FFMPEG_PATH="/usr/lib/jellyfin-ffmpeg6/ffmpeg"
elif [[ -f "/usr/bin/ffmpeg" ]]; then
    FFMPEG_PATH="/usr/bin/ffmpeg"
else
    FFMPEG_PATH=$(which ffmpeg 2>/dev/null || echo "/usr/lib/jellyfin-ffmpeg/ffmpeg")
fi

# Create systemd service file for Jellyfin
echo "> Creating systemd service for Jellyfin."
cat > /etc/systemd/system/jellyfin.service <<EOFSERVICE
[Unit]
Description=Jellyfin Media Server
After=network.target

[Service]
Type=simple
User=jellyfin
Group=jellyfin
UMask=007
WorkingDirectory=${SERVER_INSTALL_DIR}
ExecStart=${JELLYFIN_EXE} --webdir=${WEB_INTERFACE_DIR} --datadir=/var/lib/jellyfin --cachedir=/var/cache/jellyfin --logdir=/var/log/jellyfin --ffmpeg=${FFMPEG_PATH}
Restart=on-failure
RestartSec=10
TimeoutStopSec=180
TimeoutStartSec=300

[Install]
WantedBy=multi-user.target
EOFSERVICE

# Make executable if needed
chmod +x "${JELLYFIN_EXE}" 2>/dev/null || true

# Reload systemd and enable service
systemctl daemon-reload
systemctl enable jellyfin.service

cd "${TMP_DIR}"

# Clone and build custom web interface
echo "> Cloning Play TV web interface from GitHub."
git clone --depth 1 --branch "${GITHUB_BRANCH}" "https://github.com/${GITHUB_USER}/${WEB_REPO}.git" jellyfin-web
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to clone web repository from GitHub."
    exit 1
fi

cd jellyfin-web

echo "> Installing web build dependencies."
npm install
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to install npm dependencies."
    exit 1
fi

echo "> Building Play TV web interface."
npm run build:production
if [[ $? -gt 0 ]]; then
    echo "ERROR: Failed to build web interface."
    exit 1
fi

# Stop Jellyfin service before replacing files
echo "> Stopping Jellyfin service."
systemctl stop jellyfin.service 2>/dev/null || true

# Find where Jellyfin installs the web interface
POSSIBLE_WEB_DIRS=(
    "/usr/share/jellyfin/web"
    "/usr/lib/jellyfin/web"
    "/opt/jellyfin/web"
)

WEB_DIR=""
for dir in "${POSSIBLE_WEB_DIRS[@]}"; do
    if [[ -d "${dir}" ]]; then
        WEB_DIR="${dir}"
        break
    fi
done

# If not found, use default location
if [[ -z "${WEB_DIR}" ]]; then
    WEB_DIR="${WEB_INTERFACE_DIR}"
    echo "> Web interface directory not found, will create at ${WEB_DIR}"
fi

# Backup original web interface if it exists
if [[ -d "${WEB_DIR}" ]]; then
    echo "> Backing up original web interface from ${WEB_DIR}."
    mv "${WEB_DIR}" "${WEB_DIR}.backup.$(date +%Y%m%d_%H%M%S)"
fi

# Create web directory and copy files
echo "> Installing Play TV web interface to ${WEB_DIR}."
mkdir -p "${WEB_DIR}"
cp -r dist/* "${WEB_DIR}/"
chown -R jellyfin:jellyfin "${WEB_DIR}"

# Clean up
cd /
rm -rf "${TMP_DIR}"

# Start Jellyfin service
echo "> Starting Jellyfin service."
systemctl start jellyfin.service

# Wait for Jellyfin to start up
echo "> Waiting 15 seconds for Jellyfin to fully start up."
sleep 15
echo

# Output the result of systemctl status
echo "-------------------------------------------------------------------------------"
export SYSTEMD_PAGER=
systemctl status jellyfin.service || service jellyfin status
echo "-------------------------------------------------------------------------------"
echo

# Determine the IP address
GATEWAY_IFACE="$( ip route \
                  | grep '^default' \
                  | head -1 \
                  | grep -o 'dev [a-z0-9]* ' \
                  | awk '{ print $NF }' )"
IP_ADDRESS="$( ip address show dev "${GATEWAY_IFACE}" \
               | grep -w "inet .* ${GATEWAY_IFACE}$" \
               | awk '{ print $2 }' \
               | awk -F '/' '{ print $1 }' )"

# Output success message
echo "You should see the service as 'active (running)' above."
echo
echo "=========================================="
echo "  Play TV instalado com sucesso!"
echo "=========================================="
echo
echo "You can access your Play TV instance now at:"
echo "  http://${IP_ADDRESS}:8096"
echo
echo "A interface web customizada (Play TV) foi instalada e está ativa!"
echo
echo "Thank you for installing Play TV, and happy watching!"
echo

exit 0

