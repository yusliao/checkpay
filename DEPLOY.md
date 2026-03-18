# CheckPay 部署指南

## 快速开始

### 前置要求
- Docker 20.10+
- Docker Compose 2.0+

### 部署步骤

1. **克隆仓库**
```bash
git clone <repository-url>
cd checkpay
```

2. **配置环境变量**
```bash
cp .env.example .env
# 编辑.env文件，修改密码等敏感信息
```

3. **启动服务**
```bash
docker-compose up -d
```

4. **查看日志**
```bash
docker-compose logs -f web
```

5. **访问应用**
- Web应用: http://localhost:8080
- MinIO控制台: http://localhost:9001

### 初始化数据库

首次启动时，应用会自动运行EF Core迁移创建数据库表。

默认用户账号：
- 管理员: admin / admin123
- 美国财务: usfinance / usfinance123
- 大陆财务: cnfinance / cnfinance123

## 服务说明

### PostgreSQL
- 端口: 5432
- 数据库: checkpay
- 用户名: admin
- 密码: 通过环境变量DB_PASSWORD配置

### MinIO
- API端口: 9000
- 控制台端口: 9001
- 访问密钥: 通过环境变量MINIO_ROOT_USER配置
- 密钥: 通过环境变量MINIO_ROOT_PASSWORD配置
- Bucket: checkpay-files（自动创建）

### CheckPay Web
- 端口: 8080
- 环境: Production
- 自动运行数据库迁移

## 常用命令

```bash
# 启动所有服务
docker-compose up -d

# 停止所有服务
docker-compose down

# 查看服务状态
docker-compose ps

# 查看应用日志
docker-compose logs -f web

# 重启应用
docker-compose restart web

# 清理所有数据（危险操作）
docker-compose down -v
```

## 生产环境建议

1. **修改默认密码** - 在.env中设置强密码
2. **启用HTTPS** - 使用Nginx反向代理
3. **数据备份** - 定期备份postgres_data和minio_data卷
4. **资源限制** - 在docker-compose.yml中添加资源限制
5. **日志管理** - 配置日志轮转和集中收集

## 故障排查

### 应用无法启动
```bash
# 检查日志
docker-compose logs web

# 检查数据库连接
docker-compose exec postgres psql -U admin -d checkpay
```

### MinIO连接失败
```bash
# 检查MinIO状态
docker-compose logs minio

# 测试MinIO连接
curl http://localhost:9000/minio/health/live
```

### 数据库迁移失败
```bash
# 手动运行迁移
docker-compose exec web dotnet ef database update
```

