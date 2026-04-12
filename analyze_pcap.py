import os
import binascii
import subprocess
import gzip

def analyze_pcap():
    cmd = [
        "tshark", 
        "-r", "/tmp/kuaishou.pcapng",
        "-Y", "tcp.payload",
        "-T", "fields",
        "-e", "ip.src", "-e", "tcp.srcport",
        "-e", "ip.dst", "-e", "tcp.dstport",
        "-e", "tcp.payload"
    ]
    
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"Error running tshark: {result.stderr}")
        return

    lines = result.stdout.strip().split('\n')
    
    count = 0
    for line in lines:
        parts = line.split('\t')
        if len(parts) >= 5 and parts[4]:
            src_ip, src_port, dst_ip, dst_port, payload_hex = parts[:5]
            raw_data = binascii.unhexlify(payload_hex.replace(':', ''))
            
            # 过滤条件：我们只看有 GZIP 头、有快手自定义头，或者长度大于 100 的数据包
            if b'\x1f\x8b\x08' in raw_data or raw_data.startswith(b'\x01\x1a\x2b\x3c') or len(raw_data) > 100:
                print(f"\n[{count}] {src_ip}:{src_port} -> {dst_ip}:{dst_port} | Size: {len(raw_data)}")
                print(f"Hex: {raw_data[:32].hex().upper()}")
                
                # 如果是快手自定义头，打印后续字节看看有没有加密或混淆
                if raw_data.startswith(b'\x01\x1a\x2b\x3c'):
                    print("  --> Detected Kuaishou 16-byte custom header")
                    print(f"      Header bytes: {raw_data[:16].hex().upper()}")
                    
                gzip_offset = raw_data.find(b'\x1f\x8b\x08')
                if gzip_offset != -1:
                    print(f"  --> Found GZIP signature at offset {gzip_offset}")
                    try:
                        decompressed = gzip.decompress(raw_data[gzip_offset:])
                        print(f"      Decompressed size: {len(decompressed)}")
                        print(f"      Decompressed head: {decompressed[:32].hex().upper()}")
                    except Exception as e:
                        print(f"      Failed to decompress: {e}")
                
                count += 1
                if count >= 10:
                    break

analyze_pcap()