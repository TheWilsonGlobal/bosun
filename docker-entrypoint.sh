#!/bin/sh
set -e

# Start the Bosun PLC / NMEA simulator in the background unless explicitly disabled.
# This provides live Modbus TCP (port 5483) and NMEA 0183 (port 5484) feeds for the
# engine room digital twin and real-time monitoring on Render.
if [ "${START_SIMULATOR:-true}" != "false" ]; then
    echo "Starting Bosun.Simulator on 127.0.0.1:5483 (Modbus) and 127.0.0.1:5484 (NMEA)..."
    dotnet /app/simulator/Bosun.Simulator.dll --map /app/mapping/registers.json &
fi

exec dotnet Bosun.Api.dll "$@"
