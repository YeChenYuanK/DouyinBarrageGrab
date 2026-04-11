#!/usr/bin/env python3
"""解码快手WebSocket认证帧"""

import base64
import json
from pathlib import Path

def decode_auth_frame():
    # 从样本数据中提取认证帧
    sample_file = Path("/Users/yechenyuan/Dev/Unity/DouyinBarrageGrab/快手 协议ws 样本/样本数据.json")
    
    print("🔍 开始解码认证帧...")
    
    try:
        # 读取样本数据
        with open(sample_file, 'r', encoding='utf-8') as f:
            data = json.load(f)
        
        # 提取WebSocket消息
        entries = data.get('log', {}).get('entries', [])
        
        for entry in entries:
            if '_webSocketMessages' in entry:
                messages = entry['_webSocketMessages']
                for msg in messages:
                    if msg.get('type') == 'send' and msg.get('opcode') == 2:
                        print(f"✅ 找到认证帧 (time: {msg.get('time')})")
                        
                        # 解码base64数据
                        encoded_data = msg['data']
                        decoded_data = base64.b64decode(encoded_data)
                        
                        print(f"📊 原始数据长度: {len(decoded_data)} bytes")
                        print(f"🔢 十六进制预览: {decoded_data[:32].hex(' ')}")
                        
                        # 分析协议结构
                        analyze_protocol(decoded_data)
                        return
        
        print("❌ 未找到认证帧")
        
    except Exception as e:
        print(f"❌ 解码错误: {e}")

def analyze_protocol(data):
    """分析协议结构"""
    print("\n📊 协议分析:")
    print("=" * 50)
    
    # 1. 检查Protobuf特征
    is_protobuf = False
    for i in range(min(20, len(data))):
        byte = data[i]
        if 0x08 <= byte <= 0xFF:
            wire_type = byte & 0x07
            field_num = byte >> 3
            if 0 <= wire_type <= 5 and field_num > 0:
                is_protobuf = True
                print(f"🔍 可能Protobuf字段: field{field_num}, wire_type={wire_type}")
    
    if is_protobuf:
        print("✅ 检测到Protobuf格式")
    
    # 2. 检查GZIP头部
    if len(data) >= 2 and data[0] == 0x1F and data[1] == 0x8B:
        print("✅ 检测到GZIP压缩数据")
    
    # 3. 查找文本内容
    text_content = ""
    for byte in data:
        if 32 <= byte <= 126:  # 可打印ASCII
            text_content += chr(byte)
        elif text_content and len(text_content) > 3:
            break
    
    if text_content:
        print(f"📝 文本内容: {text_content}")
    
    # 4. 查找特定字段
    # 尝试查找直播间ID、token等字段
    if b'BZIafozUVCc' in data:  # 之前发现的直播间ID
        print("🎯 包含直播间ID: BZIafozUVCc")
    
    # 5. 保存解码数据
    output_file = Path("/Users/yechenyuan/Dev/Unity/DouyinBarrageGrab/.analysis/tools/decoded_auth_frame.bin")
    with open(output_file, 'wb') as f:
        f.write(data)
    
    print(f"💾 解码数据已保存: {output_file}")
    
    # 6. 显示详细hex
    print(f"\n🔬 详细Hex转储 (前64字节):")
    hex_data = data.hex(' ')
    lines = [hex_data[i:i+48] for i in range(0, min(192, len(hex_data)), 48)]
    for i, line in enumerate(lines):
        print(f"{i*16:04x}: {line}")

if __name__ == "__main__":
    decode_auth_frame()