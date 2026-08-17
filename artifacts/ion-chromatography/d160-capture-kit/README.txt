D160+ USB capture kit

ONE-TIME SETUP ON THE CONTROL COMPUTER
1. Close ShineLab.
2. Run USBPcapSetup-1.5.4.0.exe as administrator.
3. Accept the driver installation. Reboot if Windows requests it.
4. Do not copy the protocol tester again. This kit only adds the capture driver.

ONE CAPTURE RUN
1. Start ShineLab and leave it open.
2. Double-click Run-D160Capture.cmd and approve the one UAC prompt.
3. The script captures automatically for 60 seconds.
4. During the 60 seconds, perform only:
   - open D160+ communication;
   - one status/refresh read.
5. Do not load a method, inject, start, stop, reset, or change settings.
6. The script creates results\d160-capture-result-YYYYMMDD-HHMMSS.zip.
7. Copy that one ZIP back. Do not copy the individual PCAP files separately.

If the window reports that the known D160+ read request was not found, keep the
USB drive on the control computer and run the capture again. Bring back a result
only after the window says "Capture finished".

The script captures USB traffic from the available USBPcap root hubs, records the
serial-port and USB-device inventory, and writes SHA-256 hashes. It does not open
COM4 and does not inject serial data. USBPcap is used only for observation.

SOURCE
USBPcap 1.5.4.0 official release:
https://github.com/desowin/usbpcap/releases/tag/1.5.4.0

Installer SHA-256:
87A7EDF9BBBCF07B5F4373D9A192A6770D2FF3ADD7AA1E276E82E38582CCB622

The downloaded installer has a valid Authenticode signature from Tomasz Mon.
