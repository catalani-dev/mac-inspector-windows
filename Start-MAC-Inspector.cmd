@echo off
rem SPDX-FileCopyrightText: 2026 CatalaniDev <catalanidev@gmail.com>
rem SPDX-License-Identifier: LicenseRef-MAC-Inspector-1.0
rem Starts MAC Inspector for Windows in the notification area, without a console window.
start "" powershell.exe -NoProfile -WindowStyle Hidden -STA -ExecutionPolicy Bypass -File "%~dp0MAC-Inspector.ps1"
