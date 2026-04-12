import subprocess
res = subprocess.run(['/usr/local/bin/protoc', '--decode_raw'], input=open('/tmp/test6_decomp.bin', 'rb').read(), capture_output=True, text=True)
print(res.stdout)