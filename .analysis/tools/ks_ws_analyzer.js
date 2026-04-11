// 快手WebSocket协议分析脚本
const WebSocket = require('ws');

// 快手WebSocket连接信息
const KS_WS_URL = 'wss://livejs-ws.kuaishou.cn/group2';
const USER_AGENT = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36';

// 协议分析器
class KsWsAnalyzer {
    constructor() {
        this.messages = [];
        this.protocolHeaders = new Set();
        this.messageTypes = new Map();
    }

    // 连接WebSocket并开始分析
    async analyze() {
        console.log('🚀 开始分析快手WebSocket协议...');
        console.log(`📡 连接地址: ${KS_WS_URL}`);
        
        try {
            const ws = new WebSocket(KS_WS_URL, {
                headers: {
                    'User-Agent': USER_AGENT,
                    'Origin': 'https://live.kuaishou.com'
                }
            });

            ws.on('open', () => {
                console.log('✅ WebSocket连接已建立');
                console.log('⏳ 等待接收消息...');
            });

            ws.on('message', (data) => {
                this.processMessage(data);
            });

            ws.on('error', (error) => {
                console.error('❌ WebSocket错误:', error.message);
            });

            ws.on('close', () => {
                console.log('🔚 连接已关闭');
                this.generateReport();
            });

            // 10秒后自动关闭连接
            setTimeout(() => {
                ws.close();
            }, 10000);

        } catch (error) {
            console.error('❌ 连接失败:', error.message);
        }
    }

    // 处理接收到的消息
    processMessage(data) {
        try {
            let messageData;
            
            // 尝试解析二进制数据
            if (data instanceof Buffer) {
                // 快手协议通常有固定头部结构
                const header = data.slice(0, 8);
                this.protocolHeaders.add(header.toString('hex'));
                
                // 尝试GZIP解压 (offset=24)
                if (data.length > 24) {
                    try {
                        const zlib = require('zlib');
                        const decompressed = zlib.inflateSync(data.slice(24));
                        messageData = decompressed.toString();
                    } catch (e) {
                        // 如果不是GZIP，直接转为字符串
                        messageData = data.toString();
                    }
                } else {
                    messageData = data.toString();
                }
            } else {
                messageData = data;
            }

            const message = {
                timestamp: new Date().toISOString(),
                raw: data,
                parsed: messageData,
                length: data.length,
                isBinary: data instanceof Buffer
            };

            this.messages.push(message);
            
            // 分析消息类型
            this.analyzeMessageType(message);
            
            console.log(`📨 收到消息 [${message.length} bytes]:`, 
                        message.isBinary ? '二进制数据' : '文本数据');

        } catch (error) {
            console.error('❌ 消息处理错误:', error.message);
        }
    }

    // 分析消息类型
    analyzeMessageType(message) {
        if (message.isBinary) {
            // 二进制消息类型分析
            const type = this.detectBinaryType(message.raw);
            const count = this.messageTypes.get(type) || 0;
            this.messageTypes.set(type, count + 1);
        } else {
            // 文本消息类型分析
            const text = message.parsed;
            if (text.includes('heartbeat') || text.includes('ping')) {
                const count = this.messageTypes.get('heartbeat') || 0;
                this.messageTypes.set('heartbeat', count + 1);
            } else if (text.includes('danmu') || text.includes('comment')) {
                const count = this.messageTypes.get('danmaku') || 0;
                this.messageTypes.set('danmaku', count + 1);
            } else {
                const count = this.messageTypes.get('other_text') || 0;
                this.messageTypes.set('other_text', count + 1);
            }
        }
    }

    // 检测二进制消息类型
    detectBinaryType(buffer) {
        if (buffer.length < 8) return 'small_binary';
        
        // 快手心跳包特征检测
        if (this.isKsHeartbeat(buffer)) return 'heartbeat';
        
        // Protobuf消息检测
        if (this.isLikelyProtobuf(buffer)) return 'protobuf';
        
        return 'unknown_binary';
    }

    // 快手心跳包检测
    isKsHeartbeat(buffer) {
        if (buffer.length >= 30 && buffer.length <= 45) return true;
        if (buffer.length >= 16) {
            if (buffer[0] === 0x02 && buffer[1] === 0x00 && buffer[2] === 0x00) return true;
            if (buffer[0] === 0x03 && buffer[1] === 0x00 && buffer[4] === 0x00) return true;
        }
        return false;
    }

    // Protobuf检测
    isLikelyProtobuf(buffer) {
        // Protobuf通常有字段标签和wire type
        if (buffer.length < 4) return false;
        
        // 检查常见的Protobuf字段模式
        for (let i = 0; i < Math.min(buffer.length, 20); i++) {
            const byte = buffer[i];
            // Protobuf字段标签通常是变长整数
            if (byte >= 0x08 && byte <= 0xFF) {
                // 检查wire type (最后3位)
                const wireType = byte & 0x07;
                if (wireType >= 0 && wireType <= 5) {
                    return true;
                }
            }
        }
        return false;
    }

    // 生成分析报告
    generateReport() {
        console.log('\n📊 快手WebSocket协议分析报告');
        console.log('='.repeat(50));
        console.log(`📨 总消息数: ${this.messages.length}`);
        
        console.log('\n🔍 消息类型统计:');
        for (const [type, count] of this.messageTypes) {
            console.log(`   ${type}: ${count} 条`);
        }
        
        console.log('\n📋 协议头部样本:');
        this.protocolHeaders.forEach(header => {
            console.log(`   ${header}`);
        });
        
        // 保存详细数据到文件
        this.saveAnalysisData();
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
        const dataFile = path.join(dataDir, `ks_ws_analysis_${timestamp}.json`);
        
        const analysisData = {
            timestamp: new Date().toISOString(),
            url: KS_WS_URL,
            totalMessages: this.messages.length,
            messageTypes: Object.fromEntries(this.messageTypes),
            protocolHeaders: Array.from(this.protocolHeaders),
            messages: this.messages.map(msg => ({
                timestamp: msg.timestamp,
                length: msg.length,
                isBinary: msg.isBinary,
                sample: msg.isBinary ? msg.raw.slice(0, 20).toString('hex') : msg.parsed.substring(0, 100)
            }))
        };
        
        fs.writeFileSync(dataFile, JSON.stringify(analysisData, null, 2));
        console.log(`\n💾 分析数据已保存到: ${dataFile}`);
    }
}

// 执行分析
const analyzer = new KsWsAnalyzer();
analyzer.analyze().catch(console.error);