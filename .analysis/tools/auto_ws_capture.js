// 自动化WebSocket消息捕获和分析脚本
const WebSocket = require('ws');
const fs = require('fs');
const path = require('path');

// 快手WebSocket配置
const KS_WS_URL = 'wss://livejs-ws.kuaishou.cn/group5';
const CAPTURE_DURATION = 30000; // 30秒捕获时间

class AutoWsCapture {
    constructor() {
        this.messages = [];
        this.startTime = null;
        this.ws = null;
        this.captureDir = path.join(__dirname, 'captured_data');
        
        // 创建捕获目录
        if (!fs.existsSync(this.captureDir)) {
            fs.mkdirSync(this.captureDir, { recursive: true });
        }
    }

    // 启动自动化捕获
    async startCapture() {
        console.log('🚀 开始自动化WebSocket消息捕获...');
        console.log(`📡 目标URL: ${KS_WS_URL}`);
        console.log(`⏱️  捕获时长: ${CAPTURE_DURATION / 1000}秒`);
        
        this.startTime = Date.now();
        
        try {
            // 创建WebSocket连接
            this.ws = new WebSocket(KS_WS_URL, {
                headers: {
                    'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36',
                    'Origin': 'https://live.kuaishou.com',
                    'Pragma': 'no-cache',
                    'Cache-Control': 'no-cache'
                },
                rejectUnauthorized: false
            });

            this.ws.on('open', () => {
                console.log('✅ WebSocket连接已建立');
                console.log('⏳ 开始捕获消息...');
                
                // 设置捕获超时
                setTimeout(() => {
                    this.stopCapture();
                }, CAPTURE_DURATION);
            });

            this.ws.on('message', (data) => {
                this.processMessage(data);
            });

            this.ws.on('error', (error) => {
                console.error('❌ WebSocket错误:', error.message);
                this.saveCaptureData();
            });

            this.ws.on('close', () => {
                console.log('🔚 连接已关闭');
                this.saveCaptureData();
            });

        } catch (error) {
            console.error('❌ 连接失败:', error.message);
        }
    }

    // 处理接收到的消息
    processMessage(data) {
        const timestamp = new Date().toISOString();
        const elapsed = Date.now() - this.startTime;
        
        try {
            let messageData;
            let messageType = 'unknown';
            let analysis = [];

            if (data instanceof Buffer) {
                // 二进制消息处理
                messageType = 'binary';
                messageData = data.toString('hex');
                
                // 消息分析
                analysis = this.analyzeBinaryMessage(data);
                
            } else {
                // 文本消息处理
                messageType = 'text';
                messageData = data;
                analysis = this.analyzeTextMessage(data);
            }

            const message = {
                timestamp,
                elapsed,
                type: messageType,
                length: data.length,
                data: messageData,
                analysis,
                raw: data instanceof Buffer ? data.toString('base64') : data
            };

            this.messages.push(message);
            
            // 实时显示进度
            if (this.messages.length % 5 === 0) {
                console.log(`📨 已捕获 ${this.messages.length} 条消息 (${elapsed}ms)`);
            }

            // 显示重要消息分析
            if (analysis.length > 0 && !analysis.includes('心跳包')) {
                console.log(`🔍 [${messageType}] ${analysis.join(', ')}`);
            }

        } catch (error) {
            console.error('❌ 消息处理错误:', error.message);
        }
    }

    // 分析二进制消息
    analyzeBinaryMessage(buffer) {
        const analysis = [];
        const data = buffer;
        
        // 1. 检查GZIP头部
        if (data.length >= 2 && data[0] === 0x1F && data[1] === 0x8B) {
            analysis.push('GZIP压缩数据');
            
            // 尝试GZIP解压
            if (data.length > 24) {
                try {
                    const zlib = require('zlib');
                    const decompressed = zlib.inflateSync(data.slice(24));
                    const text = decompressed.toString('utf8');
                    if (text.length > 0) {
                        analysis.push(`解压成功: ${text.substring(0, 50)}...`);
                    }
                } catch (e) {
                    analysis.push('GZIP解压失败');
                }
            }
        }
        
        // 2. Protobuf检测
        if (this.isLikelyProtobuf(data)) {
            analysis.push('Protobuf格式');
            
            // 字段检测
            const fields = this.detectProtobufFields(data);
            if (fields.length > 0) {
                analysis.push(`字段: ${fields.join(', ')}`);
            }
        }
        
        // 3. 心跳包检测
        if (this.isKsHeartbeat(data)) {
            analysis.push('心跳包');
        }
        
        // 4. 文本内容检测
        const text = this.extractTextContent(data);
        if (text) {
            analysis.push(`文本: ${text}`);
        }
        
        return analysis;
    }

    // 分析文本消息
    analyzeTextMessage(text) {
        const analysis = [];
        
        if (text.includes('heartbeat') || text.includes('ping')) {
            analysis.push('心跳包');
        }
        
        if (text.includes('danmu') || text.includes('comment')) {
            analysis.push('弹幕消息');
        }
        
        // JSON解析尝试
        try {
            const json = JSON.parse(text);
            analysis.push('JSON格式');
            if (json.type) analysis.push(`类型: ${json.type}`);
        } catch (e) {
            // 不是JSON
        }
        
        return analysis;
    }

    // Protobuf检测
    isLikelyProtobuf(buffer) {
        if (buffer.length < 4) return false;
        
        for (let i = 0; i < Math.min(buffer.length, 20); i++) {
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

    // 检测Protobuf字段
    detectProtobufFields(buffer) {
        const fields = [];
        
        for (let i = 0; i < Math.min(buffer.length, 30); i++) {
            const byte = buffer[i];
            if (byte >= 0x08 && byte <= 0xFF) {
                const fieldNum = byte >> 3;
                const wireType = byte & 0x07;
                
                if (fieldNum > 0 && fieldNum <= 20) {
                    fields.push(`${fieldNum}`);
                    if (fields.length >= 3) break;
                }
            }
        }
        
        return fields;
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

    // 提取文本内容
    extractTextContent(buffer) {
        let text = '';
        for (let i = 0; i < buffer.length; i++) {
            if (buffer[i] >= 32 && buffer[i] <= 126) {
                text += String.fromCharCode(buffer[i]);
            } else if (text.length > 5) {
                break;
            }
        }
        return text.length >= 6 ? text : '';
    }

    // 停止捕获
    stopCapture() {
        console.log('🛑 停止捕获...');
        
        if (this.ws) {
            this.ws.close();
            this.ws = null;
        }
        
        this.saveCaptureData();
    }

    // 保存捕获数据
    saveCaptureData() {
        const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
        const filename = path.join(this.captureDir, `ks_ws_capture_${timestamp}.json`);
        
        const captureData = {
            url: KS_WS_URL,
            duration: Date.now() - this.startTime,
            totalMessages: this.messages.length,
            startTime: new Date(this.startTime).toISOString(),
            endTime: new Date().toISOString(),
            messages: this.messages
        };
        
        fs.writeFileSync(filename, JSON.stringify(captureData, null, 2));
        
        console.log(`💾 捕获数据已保存: ${filename}`);
        console.log(`📊 总计消息: ${this.messages.length} 条`);
        
        // 生成分析报告
        this.generateAnalysisReport(captureData);
    }

    // 生成分析报告
    generateAnalysisReport(data) {
        console.log('\n📊 协议分析报告');
        console.log('='.repeat(50));
        
        const messageTypes = {};
        const analysisCount = {};
        
        data.messages.forEach(msg => {
            messageTypes[msg.type] = (messageTypes[msg.type] || 0) + 1;
            
            msg.analysis.forEach(analysis => {
                analysisCount[analysis] = (analysisCount[analysis] || 0) + 1;
            });
        });
        
        console.log('📨 消息类型统计:');
        for (const [type, count] of Object.entries(messageTypes)) {
            console.log(`   ${type.padEnd(10)}: ${count}`);
        }
        
        console.log('\n🔍 协议特征统计:');
        for (const [analysis, count] of Object.entries(analysisCount)) {
            console.log(`   ${analysis.padEnd(20)}: ${count}`);
        }
        
        // 查找弹幕消息
        const danmuMessages = data.messages.filter(msg => 
            msg.analysis.some(a => a.includes('弹幕') || a.includes('文本'))
        );
        
        if (danmuMessages.length > 0) {
            console.log(`\n💬 发现 ${danmuMessages.length} 条可能弹幕消息`);
            danmuMessages.slice(0, 3).forEach((msg, i) => {
                console.log(`   ${i + 1}. ${msg.analysis.join(', ')}`);
            });
        }
    }
}

// 执行自动化捕获
const capture = new AutoWsCapture();
capture.startCapture().catch(console.error);

// 处理退出信号
process.on('SIGINT', () => {
    console.log('\n🛑 用户中断捕获');
    capture.stopCapture();
    process.exit(0);
});