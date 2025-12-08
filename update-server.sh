#!/usr/bin/env bash

set -e

# Configurações do Play TV
GITHUB_USER="matheusmoliveira"
SERVER_REPO="jellyfin"
WEB_REPO="jellyfin-web"
GITHUB_BRANCH="dev"
WEB_INTERFACE_DIR="/usr/share/jellyfin/web"
SERVER_INSTALL_DIR="/usr/lib/jellyfin"
TEMP_DIR="/tmp/playtv-update"

echo "=========================================="
echo "  Play TV - Atualização do Servidor"
echo "=========================================="
echo

# Check that we're root
if [[ $(whoami) != "root" ]]; then
    echo "ERROR: This script must be run as 'root' or with 'sudo'."
    exit 1
fi

# Criar diretório temporário
mkdir -p "$TEMP_DIR"
cd "$TEMP_DIR"

# 1. Parar o serviço Jellyfin
echo "> Parando serviço Jellyfin..."
systemctl stop jellyfin.service || true
sleep 2

# Verificar se o serviço parou
if systemctl is-active --quiet jellyfin.service; then
    echo "ERRO: Não foi possível parar o serviço Jellyfin"
    exit 1
fi
echo "✓ Serviço parado com sucesso"
echo

# 2. Fazer backup (opcional mas recomendado)
echo "> Criando backup..."
BACKUP_DIR="/var/backups/jellyfin-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP_DIR"
if [ -d "$SERVER_INSTALL_DIR" ]; then
    cp -r "$SERVER_INSTALL_DIR" "$BACKUP_DIR/server" 2>/dev/null || true
fi
if [ -d "$WEB_INTERFACE_DIR" ]; then
    cp -r "$WEB_INTERFACE_DIR" "$BACKUP_DIR/web" 2>/dev/null || true
fi
echo "✓ Backup criado em: $BACKUP_DIR"
echo

# 3. Atualizar o código do servidor
echo "> Clonando repositório do servidor..."
rm -rf "$SERVER_REPO"
git clone -b "$GITHUB_BRANCH" "https://github.com/${GITHUB_USER}/${SERVER_REPO}.git"
cd "$SERVER_REPO"
echo "✓ Código do servidor clonado"
echo

# 4. Compilar o servidor
echo "> Compilando servidor (isso pode levar alguns minutos)..."
dotnet build --configuration Release --no-incremental
echo "✓ Servidor compilado com sucesso"
echo

# 5. Instalar o servidor compilado
echo "> Instalando servidor..."
# Garantir que o serviço está parado
systemctl stop jellyfin.service || true
sleep 2

# Remover arquivos antigos (exceto se estiverem em uso)
if [ -f "$SERVER_INSTALL_DIR/jellyfin" ]; then
    # Tentar remover, se falhar, usar fuser para matar processos
    rm -f "$SERVER_INSTALL_DIR/jellyfin" 2>/dev/null || {
        echo "> Removendo processos que podem estar usando o arquivo..."
        fuser -k "$SERVER_INSTALL_DIR/jellyfin" 2>/dev/null || true
        sleep 2
        rm -f "$SERVER_INSTALL_DIR/jellyfin"
    }
fi

# Copiar novos arquivos
cp -r Jellyfin.Server/bin/Release/net9.0/* "$SERVER_INSTALL_DIR/"
chmod +x "$SERVER_INSTALL_DIR/jellyfin"
echo "✓ Servidor instalado"
echo

# 6. Atualizar a interface web
echo "> Clonando repositório da interface web..."
cd "$TEMP_DIR"
rm -rf "$WEB_REPO"
git clone -b "$GITHUB_BRANCH" "https://github.com/${GITHUB_USER}/${WEB_REPO}.git"
cd "$WEB_REPO"
echo "✓ Código da interface web clonado"
echo

# 7. Instalar dependências com limite de memória
echo "> Instalando dependências npm (pode levar alguns minutos)..."
# Aumentar swap temporariamente se necessário
SWAP_SIZE=$(free -m | awk '/^Swap:/ {print $2}')
if [ "$SWAP_SIZE" -lt 2048 ]; then
    echo "> Aviso: Pouca memória swap detectada. npm install pode ser lento."
fi

# Limitar uso de memória do Node.js e usar --max-old-space-size
export NODE_OPTIONS="--max-old-space-size=1024"
npm install --legacy-peer-deps 2>&1 | tee /tmp/npm-install.log || {
    echo "ERRO: Falha ao instalar dependências npm"
    echo "Verifique os logs em /tmp/npm-install.log"
    exit 1
}
echo "✓ Dependências instaladas"
echo

# 8. Compilar interface web
echo "> Compilando interface web..."
npm run build:production 2>&1 | tee /tmp/npm-build.log || {
    echo "ERRO: Falha ao compilar interface web"
    echo "Verifique os logs em /tmp/npm-build.log"
    exit 1
}

# Verificar se o build foi bem-sucedido
if [ ! -d "dist" ] || [ -z "$(ls -A dist)" ]; then
    echo "ERRO: Diretório dist não existe ou está vazio"
    exit 1
fi
echo "✓ Interface web compilada"
echo

# 9. Instalar interface web
echo "> Instalando interface web..."
# Garantir que o serviço está parado
systemctl stop jellyfin.service || true
sleep 2

# Fazer backup da interface atual
if [ -d "$WEB_INTERFACE_DIR" ]; then
    mv "$WEB_INTERFACE_DIR" "${WEB_INTERFACE_DIR}.old.$(date +%s)"
fi

# Copiar nova interface
mkdir -p "$WEB_INTERFACE_DIR"
cp -r dist/* "$WEB_INTERFACE_DIR/"
chown -R jellyfin:jellyfin "$WEB_INTERFACE_DIR" 2>/dev/null || true
echo "✓ Interface web instalada"
echo

# 10. Limpar arquivos temporários antigos
echo "> Limpando arquivos temporários..."
rm -rf "$TEMP_DIR"
echo "✓ Limpeza concluída"
echo

# 11. Reiniciar serviço
echo "> Reiniciando serviço Jellyfin..."
systemctl start jellyfin.service
sleep 3

# Verificar status
if systemctl is-active --quiet jellyfin.service; then
    echo "✓ Serviço iniciado com sucesso"
    echo
    echo "=========================================="
    echo "  Atualização concluída com sucesso!"
    echo "=========================================="
    echo
    echo "Status do serviço:"
    systemctl status jellyfin.service --no-pager -l
else
    echo "ERRO: Falha ao iniciar o serviço Jellyfin"
    echo "Verifique os logs com: journalctl -u jellyfin -n 50"
    exit 1
fi

