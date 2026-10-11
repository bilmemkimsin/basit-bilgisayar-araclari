@echo off
title SIGNAL BUS - 06
color 09
for /L %%N in (1,1,140) do @echo [SIGNAL BUS %%N] CHANNEL=%RANDOM% CHECKSUM=%RANDOM%%RANDOM% STATE=PENDING
timeout /t 2 /nobreak >nul
exit
