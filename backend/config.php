<?php
// 数据库配置（部署时按宝塔里创建的数据库信息修改）
// 管理员账号在数据库 admins 表里维护（初始 admin / admin123，登录后请立即修改）

return [
    // MySQL 连接
    'db_dsn'  => 'mysql:host=127.0.0.1;port=3306;dbname=simple_calendar;charset=utf8mb4',
    'db_user' => 'simple_calendar',
    'db_pass' => '改成你的数据库密码',
];
