import sys
from google.protobuf.internal.decoder import _DecodeVarint32

data = open('/tmp/test6_decomp.bin', 'rb').read()

def parse_pb(data, offset, depth=0, max_depth=3):
    if depth > max_depth: return
    if offset >= len(data): return
    
    end = len(data)
    i = offset
    while i < end:
        try:
            key, i = _DecodeVarint32(data, i)
            wire_type = key & 0x07
            field_num = key >> 3
            
            if wire_type == 0:
                val, i = _DecodeVarint32(data, i)
                print(f"{'  ' * depth}Field: {field_num}, Varint: {val}")
            elif wire_type == 1:
                i += 8
            elif wire_type == 2:
                length, i = _DecodeVarint32(data, i)
                if length > 0 and i + length <= end:
                    chunk = data[i:i+length]
                    try:
                        text = chunk.decode('utf-8')
                        if all(c >= ' ' or c in '\r\n\t' for c in text) and len(text) > 2:
                            print(f"{'  ' * depth}Field: {field_num}, String: {text[:100]}")
                        else:
                            print(f"{'  ' * depth}Field: {field_num}, Bytes length {length}")
                            parse_pb(chunk, 0, depth + 1, max_depth)
                    except:
                        print(f"{'  ' * depth}Field: {field_num}, Bytes length {length}")
                        parse_pb(chunk, 0, depth + 1, max_depth)
                i += length
            elif wire_type == 5:
                i += 4
            else:
                break
        except Exception as e:
            break

parse_pb(data, 0, 0, max_depth=5)
