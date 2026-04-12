import sys
import gzip
import subprocess
import os

def check_pb(filepath):
    try:
        with open(filepath, 'rb') as f:
            data = f.read()
        
        # 直接暴力搜 GZIP 头
        idx = data.find(b'\x1f\x8b\x08')
        if idx != -1:
            compressed = data[idx:]
            decompressed = None
            for i in range(10):
                try:
                    truncate_len = len(compressed) - i
                    if i > 0:
                        trunc_compressed = compressed[:truncate_len]
                    else:
                        trunc_compressed = compressed
                    decompressed = gzip.decompress(trunc_compressed)
                    break
                except Exception as e:
                    pass
                    
            if decompressed:
                print(f"Decompressed successfully: {len(decompressed)} bytes")
                # write the payload out
                with open('/tmp/decomp.bin', 'wb') as f:
                    f.write(decompressed)
            else:
                print("Failed to decompress GZIP even with truncation")
                
    except Exception as e:
        print(f'Error: {e}')

check_pb('/tmp/test2.bin')