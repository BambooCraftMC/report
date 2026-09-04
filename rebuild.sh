#!/bin/bash

PID=$(ss -tulpn | grep ':5000 ' | grep -oP 'pid=\K[0-9]+')

if [ -n "$PID" ]; then
    echo "Killing report (PID $PID)..."
    kill -9 "$PID"
fi
git pull

echo "Starting report..."
nohup dotnet run > nohup.out 2>&1 &

echo "Started with PID $!"
