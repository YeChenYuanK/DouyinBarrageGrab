import sys
import gzip
import zlib
from binascii import unhexlify

def analyze_ks_payload(hex_str):
    hex_str = "".join(hex_str.split())
    data = unhexlify(hex_str)
    
    print(f"数据总长度: {len(data)} 字节")
    print(f"数据头: {data[:32].hex().upper()}")
    
    # 查找 GZIP 标志头 1F 8B 08
    gzip_idx = data.find(b'\x1f\x8b\x08')
    if gzip_idx != -1:
        print(f"发现 GZIP 头，偏移量: {gzip_idx}")
        gzip_data = data[gzip_idx:]
        # 因为我们只是截取了文件的一部分，GZIP 会报 unexpected EOF，我们可以尝试 zlib.decompress 的容错模式
        try:
            # 尝试解压尽可能多的数据 (wbits=31 自动检测头, 即使不完整也尽量返回)
            decompressobj = zlib.decompressobj(31)
            uncompressed = decompressobj.decompress(gzip_data)
            print(f"zlib 解压成功（可能不完整）！解压后长度: {len(uncompressed)} 字节")
            print(f"解压后前 64 字节: {uncompressed[:64].hex().upper()}")
            
            # 查找中文字符串
            strings = []
            curr_str = bytearray()
            for b in uncompressed:
                if 32 <= b <= 126 or b > 127: # 包含中文范围
                    curr_str.append(b)
                else:
                    if len(curr_str) >= 4:
                        try:
                            s = curr_str.decode('utf-8')
                            if any('\u4e00' <= c <= '\u9fff' for c in s):
                                strings.append(s)
                        except:
                            pass
                    curr_str = bytearray()
            
            if strings:
                print("解压数据中包含的可能字符串 (含中文):")
                for s in strings:
                    print(f"  - {s}")
        except Exception as e:
            print(f"zlib 解压失败: {e}")

# 完整测试样本 20260412_132048_963_ws_any_raw_ksraw_103.107.218.205_kwailive_513_3b5b58ee.bin (部分)
# 从上面的终端输出，我们看到 1F 8B 08 出现在第 24 字节
sample_hex = """
01 1A 2B 3C 00 00 00 00 00 00 00 00 00 00 01 F1
08 B6 02 10 02 1A E2 03 1F 8B 08 00 00 00 00 00
02 FF 4D 51 4B 6F D3 40 10 96 0B 48 55 8F 39 21
8E 08 04 97 48 6E 9B B4 E9 1F E0 D0 9F C0 2D A1
91 40 3D 70 40 BD C7 2D A1 B6 D3 DA A8 51 1E 55
5D 55 0D 79 D2 98 3C 6A 52 DB 71 6A 89 5F 81 38
70 E9 CE 78 CD A5 77 4E 4C 58 90 7A 98 D5 CE EE
7C 8F 99 D9 AC DC 5F 7A B2 91 92 F3 6B AF 36 F2
C9 AD 7C 7A 25 99 CA A7 57 93 B9 E5 EC 7A 72 3D
97 CE AE 65 E4 95 AD 54 26 95 78 BA E8 85 0F 12
09 08 2F 50 2B 81 77 09 E6 90 3B 6D 30 AF 1E 49
2F 36 17 B7 77 B2 6F DE BD 7E BB F3 F0 67 AC 2B
DF 57 1F 7B F7 B0 DF E0 A1 01 41 01 8F EC DB 40
11 B8 A8 AD A0 DD 40 EB 33 AA 1F C1 73 98 EF 47
27 0E 15 A0 5A 45 AD CC BF 9A 37 05 85 B9 2D 2A
63 EE 94 DE F9 60 04 83 E0 6E 3D 4E 54 EE 78 37
85 5D 68 EF 0A 6C 5C 73 60 DF 8F 8C 21 0F 4F F1
B2 42 27 EF BC 27 1E AE 8D 71 AF C8 66 06 0B 6B
94 A2 3E 8B F6 CE A0 7A 1A 17 8F E6 BF 82 B9 EF
F3 81 1A D9 1A 3F 3F 60 AE 7F 1B 1C F0 C1 15 98
"""

analyze_ks_payload(sample_hex)
