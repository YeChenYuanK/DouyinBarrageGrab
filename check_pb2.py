import sys

data = open('/tmp/test6.bin', 'rb').read()
print(f"Data len: {len(data)}")

print("First 128 bytes hex:")
print(data[:128].hex())
