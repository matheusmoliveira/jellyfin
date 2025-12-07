#!/usr/bin/env bash

# Script para desinstalar Jellyfin original antes de instalar Play TV customizado

# Check that we're root; if not, fail out
if [[ $(whoami) != "root" ]]; then
    echo "ERROR: This script must be run as 'root' or with 'sudo' to function."
    echo "Try using this command instead: sudo bash uninstall-jellyfin.sh"
    exit 1
fi

echo "=========================================="
echo "  Desinstalando Jellyfin Original"
echo "=========================================="
echo

# Stop Jellyfin service
echo "> Parando serviço Jellyfin."
systemctl stop jellyfin.service 2>/dev/null || true
systemctl disable jellyfin.service 2>/dev/null || true
echo

# Remove systemd service file
echo "> Removendo arquivo de serviço systemd."
rm -f /etc/systemd/system/jellyfin.service
systemctl daemon-reload
echo

# Uninstall Jellyfin packages
echo "> Desinstalando pacotes Jellyfin."
apt remove --yes --purge jellyfin jellyfin-server jellyfin-web jellyfin-ffmpeg 2>/dev/null || true
apt autoremove --yes 2>/dev/null || true
echo

# Ask user what to do with data and configuration
echo "=========================================="
echo "  Opções de Limpeza"
echo "=========================================="
echo
echo "Você pode escolher manter ou remover:"
echo "  1. Dados do Jellyfin (/var/lib/jellyfin) - bibliotecas, metadados, etc."
echo "  2. Configurações (/etc/jellyfin) - configurações do servidor"
echo "  3. Cache (/var/cache/jellyfin) - arquivos temporários"
echo "  4. Logs (/var/log/jellyfin) - logs do servidor"
echo
echo -n "Deseja REMOVER os dados e configurações? (s/N): "
read -r REMOVE_DATA < /dev/tty

if [[ "${REMOVE_DATA,,}" =~ ^(s|sim|y|yes)$ ]]; then
    echo "> Removendo dados e configurações."
    rm -rf /var/lib/jellyfin
    rm -rf /var/cache/jellyfin
    rm -rf /var/log/jellyfin
    rm -rf /etc/jellyfin
    echo "  ✅ Dados e configurações removidos."
else
    echo "> Mantendo dados e configurações."
    echo "  📁 Dados: /var/lib/jellyfin"
    echo "  📁 Config: /etc/jellyfin"
    echo "  📁 Cache: /var/cache/jellyfin"
    echo "  📁 Logs: /var/log/jellyfin"
    echo
    echo "  ⚠️  NOTA: Você pode precisar ajustar permissões após instalar Play TV."
fi
echo

# Ask about web interface directory
echo -n "Deseja REMOVER a interface web (/usr/share/jellyfin/web)? (s/N): "
read -r REMOVE_WEB < /dev/tty

if [[ "${REMOVE_WEB,,}" =~ ^(s|sim|y|yes)$ ]]; then
    echo "> Removendo interface web."
    rm -rf /usr/share/jellyfin/web
    rm -rf /usr/lib/jellyfin/web 2>/dev/null || true
    echo "  ✅ Interface web removida."
else
    echo "> Mantendo interface web (será substituída na instalação do Play TV)."
fi
echo

# Ask about server installation directory
echo -n "Deseja REMOVER o diretório do servidor (/usr/lib/jellyfin)? (s/N): "
read -r REMOVE_SERVER < /dev/tty

if [[ "${REMOVE_SERVER,,}" =~ ^(s|sim|y|yes)$ ]]; then
    echo "> Removendo diretório do servidor."
    rm -rf /usr/lib/jellyfin
    echo "  ✅ Diretório do servidor removido."
else
    echo "> Mantendo diretório do servidor (será substituído na instalação do Play TV)."
fi
echo

# Ask about APT repository
echo -n "Deseja REMOVER o repositório APT do Jellyfin? (s/N): "
read -r REMOVE_REPO < /dev/tty

if [[ "${REMOVE_REPO,,}" =~ ^(s|sim|y|yes)$ ]]; then
    echo "> Removendo repositório APT."
    rm -f /etc/apt/sources.list.d/jellyfin.sources
    rm -f /etc/apt/sources.list.d/jellyfin.list
    rm -f /etc/apt/keyrings/jellyfin.gpg
    apt update
    echo "  ✅ Repositório APT removido."
else
    echo "> Mantendo repositório APT (útil se você quiser manter jellyfin-ffmpeg)."
fi
echo

# Check for jellyfin user
if id "jellyfin" &>/dev/null; then
    echo -n "Deseja REMOVER o usuário 'jellyfin'? (s/N): "
    read -r REMOVE_USER < /dev/tty

    if [[ "${REMOVE_USER,,}" =~ ^(s|sim|y|yes)$ ]]; then
        echo "> Removendo usuário jellyfin."
        userdel jellyfin 2>/dev/null || true
        echo "  ✅ Usuário removido."
    else
        echo "> Mantendo usuário jellyfin (será usado pelo Play TV)."
    fi
    echo
fi

echo "=========================================="
echo "  Desinstalação Concluída!"
echo "=========================================="
echo
echo "✅ Jellyfin original foi desinstalado."
echo
echo "Próximos passos:"
echo "  1. Execute o script de instalação do Play TV:"
echo "     curl https://raw.githubusercontent.com/matheusmoliveira/jellyfin-web/dev/install-playtv.sh | sudo bash"
echo
echo "  2. Ou se você já tem o script localmente:"
echo "     sudo bash install-playtv.sh"
echo
exit 0

