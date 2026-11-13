#!/usr/bin/env python3
"""Loads a built libdatachannel and creates and deletes one peer connection through its C API.

Proves the library loads on a clean machine (no missing dependencies) and that rtcConfiguration has the layout the
standalone server's P/Invoke layer assumes for the pinned release. Usage: smoke.py <path to library>
"""
import ctypes
import sys


class RtcConfiguration(ctypes.Structure):
    # rtc.h of libdatachannel v0.24.6. Other releases differ: keep in step with build.sh's pin.
    _fields_ = [
        ("iceServers", ctypes.c_void_p),
        ("iceServersCount", ctypes.c_int),
        ("proxyServer", ctypes.c_char_p),
        ("bindAddress", ctypes.c_char_p),
        ("certificateType", ctypes.c_int),
        ("iceTransportPolicy", ctypes.c_int),
        ("enableIceTcp", ctypes.c_bool),
        ("enableIceUdpMux", ctypes.c_bool),
        ("disableAutoNegotiation", ctypes.c_bool),
        ("forceMediaTransport", ctypes.c_bool),
        ("portRangeBegin", ctypes.c_uint16),
        ("portRangeEnd", ctypes.c_uint16),
        ("mtu", ctypes.c_int),
        ("maxMessageSize", ctypes.c_int),
    ]


def main():
    lib = ctypes.CDLL(sys.argv[1])
    lib.rtcCreatePeerConnection.argtypes = [ctypes.POINTER(RtcConfiguration)]
    config = RtcConfiguration()
    config.enableIceUdpMux = True
    pc = lib.rtcCreatePeerConnection(ctypes.byref(config))
    if pc < 0:
        sys.exit("rtcCreatePeerConnection failed: %d" % pc)
    dc = lib.rtcCreateDataChannel(pc, b"smoke")
    if dc < 0:
        sys.exit("rtcCreateDataChannel failed: %d" % dc)
    # Creating a channel makes the peer generate its certificate and an offer, which exercises the TLS backend.
    buf = ctypes.create_string_buffer(8192)
    length = lib.rtcGetLocalDescription(pc, buf, len(buf))
    if length <= 0 or b"fingerprint" not in buf.value:
        sys.exit("no local description with a certificate fingerprint (%d)" % length)
    if lib.rtcDeleteDataChannel(dc) < 0 or lib.rtcDeletePeerConnection(pc) < 0:
        sys.exit("delete failed")
    lib.rtcCleanup()
    print("libdatachannel smoke test passed")


if __name__ == "__main__":
    main()
