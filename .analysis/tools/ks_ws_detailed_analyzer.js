// 快手WebSocket详细协议分析脚本
const WebSocket = require('ws');
const crypto = require('crypto');

// 快手WebSocket连接配置
const KS_WS_URL = 'wss://livejs-ws.kuaishou.cn/group2';
const USER_AGENT = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36';

// 生成快手认证参数
function generateKsAuthParams() {
    const timestamp = Date.now();
    const nonce = crypto.randomBytes(8).toString('hex');
    const deviceId = crypto.createHash('md5').update(nonce).digest('hex').substring(0, 16);
    
    return {
        'appver': '10.0.0',
        'did': deviceId,
        'clientid': '1',
        'timestamp': timestamp.toString(),
        'nonce': nonce,
        'sign': generateSignature(timestamp, nonce, deviceId)
    };
}

// 生成签名（简化版）
function generateSignature(timestamp, nonce, deviceId) {
    const str = `appver=10.0.0&did=${deviceId}&clientid=1&timestamp=${timestamp}&nonce=${nonce}&key=ks_live_2021`;
    return crypto.createHash('md5').update(str).digest('hex');
}

// 协议分析器
class KsWsDetailedAnalyzer {
    constructor() {
        this.messages = [];
        this.protocolHeaders = new Set();
        this.messageTypes = new Map();
        this.connectionParams = generateKsAuthParams();
    }

    // 连接WebSocket并开始分析
    async analyze() {
        console.log('🚀 开始详细分析快手WebSocket协议...');
        console.log(`📡 连接地址: ${KS_WS_URL}`);
        console.log('🔑 认证参数:', this.connectionParams);
        
        try {
            // 构建WebSocket连接URL（带参数）
            const params = new URLSearchParams(this.connectionParams);
            const wsUrl = `${KS_WS_URL}?${params.toString()}`;
            
            const ws = new WebSocket(wsUrl, {
                headers: {
                    'User-Agent': USER_AGENT,
                    'Origin': 'https://live.kuaishou.com',
                    'Referer': 'https://live.kuaishou.com/',
                    'Sec-WebSocket-Protocol': 'chat',
                    'Pragma': 'no-cache',
                    'Cache-Control': 'no-cache'
                },
                rejectUnauthorized: false
            });

            ws.on('open', () => {
                console.log('✅ WebSocket连接已建立');
                console.log('⏳ 等待接收消息...');
                
                // 发送初始握手消息
                this.sendHandshake(ws);
            });

            ws.on('message', (data) => {
                this.processMessage(data);
            });

            ws.on('error', (error) => {
                console.error('❌ WebSocket错误:', error.message);
                if (error.code) {
                    console.error('错误代码:', error.code);
                }
            });

            ws.on('close', (code, reason) => {
                console.log(`🔚 连接已关闭, 代码: ${code}, 原因: ${reason}`);
                this.generateReport();
            });

            // 30秒后自动关闭连接
            setTimeout(() => {
                if (ws.readyState === WebSocket.OPEN) {
                    ws.close();
                }
            }, 30000);

        } catch (error) {
            console.error('❌ 连接失败:', error.message);
            if (error.stack) {
                console.error('堆栈:', error.stack);
            }
        }
    }

    // 发送握手消息
    sendHandshake(ws) {
        try {
            // 快手初始握手消息格式（基于常见模式）
            const handshakeMsg = JSON.stringify({
                "type": "init",
                "data": {
                    "appver": "10.0.0",
                    "platform": "web",
                    "did": this.connectionParams.did,
                    "clientid": "1",
                    "token": "",
                    "version": "1.0"
                }
            });
            
            ws.send(handshakeMsg);
            console.log('🤝 发送握手消息:', handshakeMsg);
            
        } catch (error) {
            console.error('❌ 握手消息发送失败:', error.message);
        }
    }

    // 处理接收到的消息
    processMessage(data) {
        try {
            let messageData;
            let messageType = 'unknown';
            
            // 尝试解析二进制数据
            if (data instanceof Buffer) {
                // 分析协议头部
                const header = data.slice(0, Math.min(12, data.length));
                const headerHex = header.toString('hex');
                this.protocolHeaders.add(headerHex);
                
                // 检测消息类型
                messageType = this.detectMessageType(data);
                
                // 尝试GZIP解压
                if (data.length > 24) {
                    try {
                        const zlib = require('zlib');
                        const decompressed = zlib.inflateSync(data.slice(24));
                        messageData = decompressed.toString('utf8');
                    } catch (e) {
                        // 如果不是GZIP，尝试Protobuf解析或直接hex显示
                        messageData = data.toString('hex');
                    }
                } else {
                    messageData = data.toString('hex');
                }
            } else {
                messageData = data;
                messageType = 'text';
                
                // 分析文本消息类型
                if (typeof data === 'string') {
                    if (data.includes('heartbeat') || data.includes('ping')) {
                        messageType = 'heartbeat';
                    } else if (data.includes('danmu') || data.includes('comment')) {
                        messageType = 'danmaku';
                    } else if (data.includes('init') || data.includes('connect')) {
                        messageType = 'handshake';
                    }
                }
            }

            const message = {
                timestamp: new Date().toISOString(),
                raw: data,
                parsed: messageData,
                length: data.length,
                isBinary: data instanceof Buffer,
                type: messageType
            };

            this.messages.push(message);
            
            // 更新消息类型统计
            const count = this.messageTypes.get(messageType) || 0;
            this.messageTypes.set(messageType, count + 1);
            
            console.log(`📨 收到${messageType}消息 [${message.length} bytes]`);
            
            // 如果是弹幕消息，尝试提取详细信息
            if (messageType === 'danmaku' || messageType === 'protobuf') {
                this.extractDanmakuInfo(message);
            }

        } catch (error) {
            console.error('❌ 消息处理错误:', error.message);
        }
    }

    // 检测消息类型
    detectMessageType(buffer) {
        if (!(buffer instanceof Buffer)) return 'text';
        
        if (buffer.length < 4) return 'small_binary';
        
        // 快手心跳包特征
        if (this.isKsHeartbeat(buffer)) return 'heartbeat';
        
        // Protobuf消息检测
        if (this.isLikelyProtobuf(buffer)) return 'protobuf';
        
        // GZIP压缩数据检测
        if (this.isGzipData(buffer)) return 'gzip';
        
        return 'unknown_binary';
    }

    // 快手心跳包检测
    isKsHeartbeat(buffer) {
        if (buffer.length >= 30 && buffer.length <= 45) return true;
        if (buffer.length >= 16) {
            // 常见心跳包模式
            if (buffer[0] === 0x02 && buffer[1] === 0x00 && buffer[2] === 0x00) return true;
            if (buffer[0] === 0x03 && buffer[1] === 0x00 && buffer[4] === 0x00) return true;
            if (buffer.readUInt32BE(0) === 0x00000008) return true; // Protobuf field 1
        }
        return false;
    }

    // Protobuf检测
    isLikelyProtobuf(buffer) {
        if (buffer.length < 4) return false;
        
        // 检查常见的Protobuf字段模式
        for (let i = 0; i < Math.min(buffer.length, 32); i++) {
            const byte = buffer[i];
            if (byte >= 0x08 && byte <= 0xFF) {
                const wireType = byte & 0x07;
                if (wireType >= 0 && wireType <= 5) {
                    return true;
                }
            }
        }
        return false;
    }

    // GZIP数据检测
    isGzipData(buffer) {
        if (buffer.length < 10) return false;
        // GZIP魔术头: 0x1f 0x8b
        return buffer[0] === 0x1F && buffer[1] === 0x8B;
    }

    // 提取弹幕信息
    extractDanmakuInfo(message) {
        try {
            if (message.isBinary && message.parsed && typeof message.parsed === 'string') {
                // 尝试从二进制数据中查找文本信息
                const text = message.parsed.toLowerCase();
                
                // 查找可能的昵称字段
                if (text.includes('nick') || text.includes('name')) {
                    console.log('   👤 可能包含昵称信息');
                }
                
                // 查找可能的内容字段
                if (text.includes('content') || text.includes('text')) {
                    console.log('   💬 可能包含弹幕内容');
                }
                
                // 查找时间戳字段
                if (text.includes('time') || text.includes('stamp')) {
                    console.log('   ⏰ 可能包含时间戳');
                }
            }
        } catch (error) {
            // 忽略提取错误
        }
    }

    // 生成分析报告
    generateReport() {
        console.log('\n📊 快手WebSocket详细协议分析报告');
        console.log('='.repeat(60));
        console.log(`📨 总消息数: ${this.messages.length}`);
        
        console.log('\n🔍 消息类型统计:');
        for (const [type, count] of this.messageTypes) {
            console.log(`   ${type.padEnd(15)}: ${count} 条`);
        }
        
        console.log('\n📋 协议头部样本:');
        let headerCount = 0;
        this.protocolHeaders.forEach(header => {
            if (headerCount < 5) { // 只显示前5个样本
                console.log(`   ${header}`);
            }
            headerCount++;
        });
        if (headerCount > 5) {
            console.log(`   ... 还有 ${headerCount - 5} 个头部样本`);
        }
        
        // 显示一些消息样本
        console.log('\n🔬 消息样本:');
        const sampleMessages = this.messages.slice(0, 3);
        sampleMessages.forEach((msg, index) => {
            console.log(`\n   样本 ${index + 1} (${msg.type}):`);
            console.log(`     长度: ${msg.length} bytes`);
            console.log(`     类型: ${msg.isBinary ? '二进制' : '文本'}`);
            if (msg.parsed && typeof msg.parsed === 'string' && msg.parsed.length > 0) {
                const preview = msg.parsed.length > 100 ? msg.parsed.substring(0, 100) + '...' : msg.parsed;
                console.log(`     预览: ${preview}`);
            }
        });
        
        // 保存详细数据到文件
        this.saveAnalysisData();
        
        // 生成协议分析建议
        this.generateProtocolSuggestions();
    }

    // 生成协议分析建议
    generateProtocolSuggestions() {
        console.log('\n💡 协议分析建议:');
        
        if (this.messages.length === 0) {
            console.log('   ❗ 未收到任何消息，可能需要：');
            console.log('      - 正确的认证参数');
            console.log('      - 特定的握手协议');
            console.log('      - 直播间特定的订阅消息');
            return;
        }
        
        // 根据收到的消息类型给出建议
        if (this.messageTypes.has('heartbeat')) {
            console.log('   ✅ 检测到心跳包，连接正常');
        }
        
        if (this.messageTypes.has('protobuf')) {
            console.log('   🔍 检测到Protobuf消息，需要：');
            console.log('      - Protobuf schema分析');
            console.log('      - 字段路径映射');
        }
        
        if (this.messageTypes.has('gzip')) {
            console.log('   📦 检测到GZIP压缩数据，offset=24');
        }
        
        if (this.messageTypes.has('danmaku')) {
            console.log('   🎯 检测到弹幕消息，可以提取：');
            console.log('      - 昵称 (nickname)');
            console.log('      - 内容 (content)');
            console.log('      - 时间戳 (timestamp)');
        }
    }

    // 保存分析数据
    saveAnalysisData() {
        const fs = require('fs');
        const path = require('path');
        
        const dataDir = path.join(__dirname, 'analysis_data');
        if (!fs.existsSync(dataDir)) {
            fs.mkdirSync(dataDir, { recursive: true });
        }
        
        const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
        const dataFile = path.join(dataDir, `ks_ws_detailed_analysis_${timestamp}.json`);
        
        const analysisData = {
            timestamp: new Date().toISOString(),
            url: KS_WS_URL,
            authParams: this.connectionParams,
            totalMessages: this.messages.length,
            messageTypes: Object.fromEntries(this.messageTypes),
            protocolHeaders: Array.from(this.protocolHeaders),
            messages: this.messages.map(msg => ({
                timestamp: msg.timestamp,
                type: msg.type,
                length: msg.length,
                isBinary: msg.isBinary,
                sample: msg.isBinary ? 
                    msg.raw.slice(0, Math.min(32, msg.raw.length)).toString('hex') : 
                    (typeof msg.parsed === 'string' ? msg.parsed.substring(0, 200) : String(msg.parsed))
            }))
        };
        
        fs.writeFileSync(dataFile, JSON.stringify(analysisData, null, 2));
        console.log(`\n💾 详细分析数据已保存到: ${dataFile}`);
    }
}

// 执行详细分析
const analyzer = new KsWsDetailedAnalyzer();
analyzer.analyze().catch(console.error);