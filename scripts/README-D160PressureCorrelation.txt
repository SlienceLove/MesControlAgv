D160+ pressure correlation capture

This package is passive. It does not open COM4 and does not send Modbus data.
It requires USBPcap to already be installed on the control computer.

1. Close no programs. Start ShineLab and leave it running.
2. Double-click Run-D160PressureCorrelation.cmd and approve the UAC prompt.
3. The package uses the confirmed ShineLab pressure unit MPa automatically.
4. During an already-approved, supervised operation, enter at least five
   stable pressure values when prompted. Do not change settings only for this
   capture. Enter optional flow and operation notes when useful.
5. Return only the ZIP from d160-pressure-correlation-results.

The result contains USBPcap PCAP data, timestamps, user-entered display values,
the serial-port inventory, a process-read presence check, and SHA-256 hashes.
It is not a write or activation test.
