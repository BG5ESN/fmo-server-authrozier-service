#!/bin/bash
# FMO SAS 一键安装脚本（Linux / macOS）
# 用法: curl -fsSL <url>/install.sh | bash
# 或:   SAS_VERSION=v1.0.0 curl -fsSL <url>/install.sh | bash

set -e

# ── 可配置变量 ──
BASE_URL="${SAS_BASE_URL:-https://cdn.example.com/sas}"
VERSION="${SAS_VERSION:-latest}"
INSTALL_DIR="${HOME}/.local/bin"

# ── 平台检测 → RID ──
OS=$(uname -s)
ARCH=$(uname -m)

case "$OS-$ARCH" in
    Linux-x86_64)  RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    Darwin-x86_64) RID="osx-x64" ;;
    Darwin-arm64)  RID="osx-arm64" ;;
    *)
        echo "Unsupported platform: $OS $ARCH"
        exit 1
        ;;
esac

echo "Platform: $OS $ARCH → $RID"

# ── 下载 + 解压 ──
URL="$BASE_URL/$VERSION/sas-$RID.tar.gz"
TAR="/tmp/sas-install.tar.gz"
EXTRACT="/tmp/sas-install"

echo "Downloading $URL ..."
curl -fsSL "$URL" -o "$TAR"

rm -rf "$EXTRACT"
mkdir -p "$EXTRACT"
tar xzf "$TAR" -C "$EXTRACT"

# ── 安装 ──
mkdir -p "$INSTALL_DIR"
cp "$EXTRACT/Sas" "$INSTALL_DIR/sas"
chmod +x "$INSTALL_DIR/sas"

# 内置 Root CA
if [ -d "$EXTRACT/builtin" ]; then
    rm -rf "$INSTALL_DIR/builtin"
    cp -r "$EXTRACT/builtin" "$INSTALL_DIR/"
fi

# ── 清理 ──
rm -rf "$TAR" "$EXTRACT"

# ── 提示 ──
echo
echo "═══ SAS installed ═══"
echo "  Binary: $INSTALL_DIR/sas"
echo "  Run 'sas' to start interactive setup."
echo
if [ "$INSTALL_DIR" = "$HOME/.local/bin" ]; then
    if ! echo "$PATH" | grep -q "$HOME/.local/bin"; then
        echo "  Note: add ~/.local/bin to PATH if not already:"
        echo "    echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.bashrc"
    fi
fi
