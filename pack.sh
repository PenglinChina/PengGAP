#! /bin/bash
cd ./publish

# 只对存在的文件改权限，缺失时跳过而不是让脚本报错
chmod_if_exists() {
	if [ -f "$1" ]; then
		chmod 777 "$1"
	fi
	return 0
}

# win-x64
zip -r penggap_win-x64.zip penggap_win-x64

# linux-x64
chmod_if_exists ./penggap_linux-x64/penggap
chmod_if_exists ./penggap_linux-x64/dnscrypt-proxy/dnscrypt-proxy
zip -r penggap_linux-x64.zip penggap_linux-x64

# linux-arm64
chmod_if_exists ./penggap_linux-arm64/penggap
chmod_if_exists ./penggap_linux-arm64/dnscrypt-proxy/dnscrypt-proxy
zip -r penggap_linux-arm64.zip penggap_linux-arm64

# osx-x64
chmod_if_exists ./penggap_osx-x64/penggap
chmod_if_exists ./penggap_osx-x64/dnscrypt-proxy/dnscrypt-proxy
zip -r penggap_osx-x64.zip penggap_osx-x64

# osx-arm64
chmod_if_exists ./penggap_osx-arm64/penggap
chmod_if_exists ./penggap_osx-arm64/dnscrypt-proxy/dnscrypt-proxy
zip -r penggap_osx-arm64.zip penggap_osx-arm64
