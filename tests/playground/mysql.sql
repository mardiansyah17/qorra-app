/*
  Qorra playground for MySQL 8.0.17+: every column type, edge values, constraints, odd names,
  big / wide tables, engines, partitions, spatial / fulltext / multi-valued indexes, views and programmability.
  Re-runnable: drops and recreates the qorra_playground database (only that one).

  mysql --user=root --host=localhost --default-character-set=utf8mb4 -e "source tests/playground/mysql.sql"
*/
DROP DATABASE IF EXISTS qorra_playground;
CREATE DATABASE qorra_playground CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE qorra_playground;

SET NAMES utf8mb4;
SET SESSION time_zone = '+00:00';
SET SESSION sql_mode = 'STRICT_ALL_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,ONLY_FULL_GROUP_BY,NO_ENGINE_SUBSTITUTION';
SET SESSION cte_max_recursion_depth = 1000000;
SET SESSION group_concat_max_len = 1000000;

/* ============================== 1. Every column type ============================== */

CREATE TABLE all_types (
    id                  INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    label               VARCHAR(40) NOT NULL,
    c_tinyint           TINYINT,
    c_tinyint_u         TINYINT UNSIGNED,
    c_tinyint1          TINYINT(1)              COMMENT 'Drivers often read this as boolean',
    c_smallint          SMALLINT,
    c_smallint_u        SMALLINT UNSIGNED,
    c_mediumint         MEDIUMINT,
    c_mediumint_u       MEDIUMINT UNSIGNED,
    c_int               INT,
    c_int_u             INT UNSIGNED,
    c_int_zerofill      INT(6) ZEROFILL,
    c_bigint            BIGINT,
    c_bigint_u          BIGINT UNSIGNED,
    c_bool              BOOLEAN,
    c_bit1              BIT(1),
    c_bit64             BIT(64),
    c_decimal           DECIMAL(65,30),
    c_numeric           NUMERIC(10,2),
    c_float             FLOAT,
    c_double            DOUBLE,
    c_date              DATE,
    c_time              TIME(6),
    c_datetime          DATETIME(6),
    c_timestamp         TIMESTAMP(6) NULL,
    c_year              YEAR,
    c_char              CHAR(10),
    c_varchar           VARCHAR(100),
    c_tinytext          TINYTEXT,
    c_text              TEXT,
    c_mediumtext        MEDIUMTEXT,
    c_longtext          LONGTEXT,
    c_binary            BINARY(8),
    c_varbinary         VARBINARY(64),
    c_tinyblob          TINYBLOB,
    c_blob              BLOB,
    c_mediumblob        MEDIUMBLOB,
    c_longblob          LONGBLOB,
    c_enum              ENUM('small', 'medium', 'large'),
    c_set               SET('red', 'green', 'blue'),
    c_json              JSON,
    c_geometry          GEOMETRY,
    c_point             POINT,
    c_linestring        LINESTRING,
    c_polygon           POLYGON,
    c_multipoint        MULTIPOINT,
    c_multilinestring   MULTILINESTRING,
    c_multipolygon      MULTIPOLYGON,
    c_geomcollection    GEOMETRYCOLLECTION,
    c_uuid_bin          BINARY(16),
    c_uuid_char         CHAR(36) CHARACTER SET ascii,
    c_stored            BIGINT AS (c_int * 2) STORED,
    c_virtual           VARCHAR(100) AS (UPPER(c_varchar)) VIRTUAL,
    c_invisible         INT DEFAULT 7 INVISIBLE
) COMMENT = 'One column per MySQL type';

INSERT INTO all_types SET label = 'typical',
    c_tinyint = -5, c_tinyint_u = 200, c_tinyint1 = 1, c_smallint = 123, c_smallint_u = 60000, c_mediumint = -12345,
    c_mediumint_u = 16000000, c_int = 42, c_int_u = 4000000000, c_int_zerofill = 42, c_bigint = 1234567890123,
    c_bigint_u = 12345678901234567890, c_bool = TRUE, c_bit1 = b'1', c_bit64 = b'1010',
    c_decimal = 12345.6789012345, c_numeric = 99.95, c_float = 2.71828, c_double = 3.14159265358979,
    c_date = '2026-11-22', c_time = '13:45:30.123456', c_datetime = '2026-11-22 13:45:30.123456',
    c_timestamp = '2026-11-22 13:45:30.123456', c_year = 2026,
    c_char = 'abc', c_varchar = 'Hello world', c_tinytext = 'tiny', c_text = 'text value', c_mediumtext = 'medium value', c_longtext = 'long value',
    c_binary = x'0102030405060708', c_varbinary = x'DEADBEEF', c_tinyblob = x'CAFE', c_blob = x'CAFEBABE',
    c_mediumblob = x'89504E470D0A1A0A', c_longblob = x'00FF',
    c_enum = 'medium', c_set = 'red,blue', c_json = '{"name":"Qorra","tags":["a","b"],"n":1.5,"nested":{"ok":true}}',
    c_geometry = ST_GeomFromText('POINT(106.8456 -6.2088)', 4326, 'axis-order=long-lat'),
    c_point = POINT(1, 2),
    c_linestring = ST_GeomFromText('LINESTRING(0 0, 1 1, 2 0)'),
    c_polygon = ST_GeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))'),
    c_multipoint = ST_GeomFromText('MULTIPOINT((1 1), (2 2))'),
    c_multilinestring = ST_GeomFromText('MULTILINESTRING((0 0, 1 1), (2 2, 3 3))'),
    c_multipolygon = ST_GeomFromText('MULTIPOLYGON(((0 0, 1 0, 1 1, 0 1, 0 0)), ((2 2, 3 2, 3 3, 2 3, 2 2)))'),
    c_geomcollection = ST_GeomFromText('GEOMETRYCOLLECTION(POINT(1 1), LINESTRING(0 0, 1 1))'),
    c_uuid_bin = UUID_TO_BIN('6F9619FF-8B86-D011-B42D-00C04FC964FF'), c_uuid_char = '6f9619ff-8b86-d011-b42d-00c04fc964ff';

INSERT INTO all_types SET label = 'minimum',
    c_tinyint = -128, c_tinyint_u = 0, c_tinyint1 = -128, c_smallint = -32768, c_smallint_u = 0, c_mediumint = -8388608,
    c_mediumint_u = 0, c_int = -2147483648, c_int_u = 0, c_int_zerofill = 0, c_bigint = -9223372036854775808,
    c_bigint_u = 0, c_bool = FALSE, c_bit1 = b'0', c_bit64 = b'0',
    c_decimal = -99999999999999999999999999999999999.999999999999999999999999999999, c_numeric = -99999999.99,
    c_float = -3.40282E+38, c_double = -1.7976931348623157E+308,
    c_date = '1000-01-01', c_time = '-838:59:59.000000', c_datetime = '1000-01-01 00:00:00.000000',
    c_timestamp = '1970-01-01 00:00:01.000000', c_year = 1901,
    c_char = '', c_varchar = '', c_tinytext = '', c_text = '', c_mediumtext = '', c_longtext = '',
    c_binary = x'', c_varbinary = x'', c_tinyblob = x'', c_blob = x'', c_mediumblob = x'', c_longblob = x'',
    c_enum = 'small', c_set = '', c_json = '{}',
    c_geometry = ST_GeomFromText('POINT(0 0)'),
    c_point = POINT(0, 0),
    c_linestring = ST_GeomFromText('LINESTRING(0 0, 1 1)'),
    c_polygon = ST_GeomFromText('POLYGON((0 0, 1 0, 0 1, 0 0))'),
    c_multipoint = ST_GeomFromText('MULTIPOINT((0 0))'),
    c_multilinestring = ST_GeomFromText('MULTILINESTRING((0 0, 1 1))'),
    c_multipolygon = ST_GeomFromText('MULTIPOLYGON(((0 0, 1 0, 0 1, 0 0)))'),
    c_geomcollection = ST_GeomFromText('GEOMETRYCOLLECTION EMPTY'),
    c_uuid_bin = UUID_TO_BIN('00000000-0000-0000-0000-000000000000'), c_uuid_char = '00000000-0000-0000-0000-000000000000';

INSERT INTO all_types SET label = 'maximum',
    c_tinyint = 127, c_tinyint_u = 255, c_tinyint1 = 127, c_smallint = 32767, c_smallint_u = 65535, c_mediumint = 8388607,
    c_mediumint_u = 16777215, c_int = 2147483647, c_int_u = 4294967295, c_int_zerofill = 4294967295, c_bigint = 9223372036854775807,
    c_bigint_u = 18446744073709551615, c_bool = TRUE, c_bit1 = b'1',
    c_bit64 = b'1111111111111111111111111111111111111111111111111111111111111111',
    c_decimal = 99999999999999999999999999999999999.999999999999999999999999999999, c_numeric = 99999999.99,
    c_float = 3.40282E+38, c_double = 1.7976931348623157E+308,
    c_date = '9999-12-31', c_time = '838:59:59.000000', c_datetime = '9999-12-31 23:59:59.999999',
    c_timestamp = '2038-01-19 03:14:07.999999', c_year = 2155,
    c_char = 'ZZZZZZZZZZ', c_varchar = REPEAT('x', 100), c_tinytext = REPEAT('t', 255), c_text = REPEAT('T', 65535),
    c_mediumtext = REPEAT('Lorem ipsum dolor sit amet. ', 20000), c_longtext = REPEAT('long text ', 200000),
    c_binary = x'FFFFFFFFFFFFFFFF', c_varbinary = UNHEX(REPEAT('FF', 64)), c_tinyblob = UNHEX(REPEAT('AB', 255)),
    c_blob = REPEAT('B', 65535), c_mediumblob = REPEAT('A', 100000), c_longblob = REPEAT('L', 1000000),
    c_enum = 'large', c_set = 'red,green,blue', c_json = CONCAT('[', REPEAT('{"k":1},', 500), '{"k":2}]'),
    c_geometry = ST_GeomFromText('LINESTRING(0 0, 100 100, 200 0)'),
    c_point = POINT(1e10, -1e10),
    c_linestring = ST_GeomFromText('LINESTRING(0 0, 10 10, 20 0, 30 10, 40 0, 50 10)'),
    c_polygon = ST_GeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 8 2, 8 8, 2 8, 2 2))'),
    c_multipoint = ST_GeomFromText('MULTIPOINT((1 1), (2 2), (3 3), (4 4), (5 5))'),
    c_multilinestring = ST_GeomFromText('MULTILINESTRING((0 0, 1 1), (2 2, 3 3), (4 4, 5 5))'),
    c_multipolygon = ST_GeomFromText('MULTIPOLYGON(((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 8 2, 8 8, 2 8, 2 2)), ((20 20, 30 20, 30 30, 20 20)))'),
    c_geomcollection = ST_GeomFromText('GEOMETRYCOLLECTION(POINT(1 1), LINESTRING(0 0, 1 1), POLYGON((0 0, 1 0, 0 1, 0 0)), GEOMETRYCOLLECTION(POINT(2 2)))'),
    c_uuid_bin = UUID_TO_BIN('FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'), c_uuid_char = 'ffffffff-ffff-ffff-ffff-ffffffffffff';

INSERT INTO all_types (label) VALUES ('all null');

INSERT INTO all_types SET label = 'tricky text',
    c_tinyint = 0, c_tinyint_u = 0, c_tinyint1 = 2, c_smallint = 0, c_smallint_u = 0, c_mediumint = 0,
    c_mediumint_u = 0, c_int = 0, c_int_u = 0, c_int_zerofill = 7, c_bigint = 0, c_bigint_u = 0, c_bool = FALSE, c_bit1 = b'0', c_bit64 = b'101',
    c_decimal = 0, c_numeric = 0, c_float = -0.0, c_double = -0.0E0,
    c_date = '2000-02-29', c_time = '-00:00:01', c_datetime = '2000-02-29 12:00', c_timestamp = '2000-02-29 12:00', c_year = 2000,
    c_char = 'NULL', c_varchar = 'it''s "quoted" `back` [bracket] \\ backslash',
    c_tinytext = 'line1\r\nline2\nline3', c_text = '\ttab\tseparated',
    c_mediumtext = '😀🚀 العربية 日本語 Ελληνικά Tiếng Việt', c_longtext = '<script>alert(1)</script>''; DROP TABLE x; --',
    c_binary = x'00', c_varbinary = x'00FF00FF', c_tinyblob = x'0D0A', c_blob = 'plain text stored in a blob',
    c_mediumblob = x'89504E47', c_longblob = CONVERT('😀 utf8 bytes' USING utf8mb4),
    c_enum = 'small', c_set = 'green', c_json = '{"quote":"\\"","unicode":"😀","newline":"a\\nb","backslash":"C:\\\\path"}',
    c_geometry = ST_GeomFromText('MULTIPOINT((1 1), (2 2))'),
    c_point = POINT(-0.0, 0),
    c_linestring = ST_GeomFromText('LINESTRING(106.8 -6.2, 107.6 -6.9)'),
    c_polygon = ST_GeomFromText('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))'),
    c_multipoint = ST_GeomFromText('MULTIPOINT((0 0), (0 0))'),
    c_multilinestring = ST_GeomFromText('MULTILINESTRING((0 0, 1 1))'),
    c_multipolygon = ST_GeomFromText('MULTIPOLYGON(((0 0, 1 0, 0 1, 0 0)))'),
    c_geomcollection = ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))'),
    c_uuid_bin = UUID_TO_BIN(UUID()), c_uuid_char = UUID();

INSERT INTO all_types SET label = 'precision',
    c_tinyint = 1, c_tinyint_u = 1, c_tinyint1 = 0, c_smallint = 1, c_smallint_u = 1, c_mediumint = 1,
    c_mediumint_u = 1, c_int = 1, c_int_u = 1, c_int_zerofill = 1, c_bigint = 1, c_bigint_u = 1, c_bool = TRUE, c_bit1 = b'1', c_bit64 = b'1',
    c_decimal = 0.000000000000000000000000000001, c_numeric = 0.01, c_float = 0.1, c_double = 0.1E0 + 0.2E0,
    c_date = '1970-01-01', c_time = '00:00:00.000001', c_datetime = '1970-01-01 00:00:00.000001',
    c_timestamp = '1970-01-01 00:00:01.000001', c_year = 1970,
    c_char = '1', c_varchar = '0.1', c_tinytext = '1e10', c_text = '-0', c_mediumtext = '0.30000000000000004', c_longtext = 'NaN',
    c_binary = x'01', c_varbinary = x'01', c_tinyblob = x'01', c_blob = x'01', c_mediumblob = x'01', c_longblob = x'01',
    c_enum = 'medium', c_set = 'blue,red', c_json = '[]',
    c_geometry = ST_GeomFromText('POINT(0.1 0.2)'),
    c_point = POINT(0.1, 0.2),
    c_linestring = ST_GeomFromText('LINESTRING(0.1 0.1, 0.2 0.2)'),
    c_polygon = ST_GeomFromText('POLYGON((0 0, 0.1 0, 0 0.1, 0 0))'),
    c_multipoint = ST_GeomFromText('MULTIPOINT((0.1 0.1))'),
    c_multilinestring = ST_GeomFromText('MULTILINESTRING((0.1 0.1, 0.2 0.2))'),
    c_multipolygon = ST_GeomFromText('MULTIPOLYGON(((0 0, 0.1 0, 0 0.1, 0 0)))'),
    c_geomcollection = ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0.1 0.2))'),
    c_uuid_bin = UUID_TO_BIN(UUID(), 1), c_uuid_char = UUID();

CREATE TABLE zero_dates (
    id      INT AUTO_INCREMENT PRIMARY KEY,
    note    VARCHAR(40) NOT NULL,
    d       DATE NULL,
    dt      DATETIME NULL,
    ts      TIMESTAMP NULL,
    y       YEAR NULL
);
SET @saved_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION';
INSERT INTO zero_dates (note, d, dt, ts, y) VALUES
('all zero', '0000-00-00', '0000-00-00 00:00:00', '0000-00-00 00:00:00', 0),
('zero month/day', '2026-00-15', '2026-11-00 10:00:00', NULL, 2026),
('normal', '2026-11-22', '2026-11-22 10:00:00', '2026-11-22 10:00:00', 2026);
SET SESSION sql_mode = @saved_mode;

/* ============================== 2. Relations and constraints ============================== */

CREATE TABLE customers (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    full_name   VARCHAR(120) NOT NULL,
    email       VARCHAR(200) NOT NULL COMMENT 'Email unik, dipakai untuk login',
    phone       VARCHAR(20) NULL,
    status      ENUM('active', 'inactive', 'banned') NOT NULL DEFAULT 'active',
    country     CHAR(2) NOT NULL DEFAULT 'ID',
    birth_date  DATE NULL,
    created_at  DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at  DATETIME(3) NULL ON UPDATE CURRENT_TIMESTAMP(3),
    CONSTRAINT uq_customers_email UNIQUE (email),
    CONSTRAINT ck_customers_birth CHECK (birth_date >= '1900-01-01')
) COMMENT = 'Pelanggan toko';

CREATE TABLE products (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    sku         VARCHAR(30) NOT NULL,
    name        VARCHAR(150) NOT NULL,
    category    VARCHAR(50) NULL,
    price       DECIMAL(12,2) NOT NULL COMMENT 'Harga dalam rupiah',
    stock       INT NOT NULL DEFAULT 0,
    is_active   TINYINT(1) NOT NULL DEFAULT 1,
    attrs       JSON NULL,
    CONSTRAINT uq_products_sku UNIQUE (sku),
    CONSTRAINT ck_products_price CHECK (price >= 0),
    CONSTRAINT ck_products_stock_active CHECK (stock >= 0 OR is_active = 0),
    INDEX ix_products_category (category, price DESC),
    INDEX ix_products_name_prefix (name(20)),
    INDEX ix_products_lower_sku ((LOWER(sku))),
    INDEX ix_products_tags ((CAST(attrs->'$.tags' AS CHAR(20) ARRAY))),
    INDEX ix_products_stock (stock) INVISIBLE,
    FULLTEXT INDEX ft_products_name (name)
);

CREATE TABLE orders (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    order_no    VARCHAR(20) NOT NULL,
    customer_id INT NOT NULL,
    status      VARCHAR(12) NOT NULL DEFAULT 'pending',
    ordered_at  DATETIME NOT NULL,
    updated_at  TIMESTAMP(3) NULL DEFAULT NULL ON UPDATE CURRENT_TIMESTAMP(3),
    notes       TEXT NULL,
    CONSTRAINT uq_orders_no UNIQUE (order_no),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT ck_orders_status CHECK (status IN ('pending', 'paid', 'shipped', 'cancelled')),
    INDEX ix_orders_customer_date (customer_id, ordered_at DESC)
);

CREATE TABLE order_items (
    order_id    INT NOT NULL,
    line_no     SMALLINT NOT NULL,
    product_id  INT NOT NULL,
    quantity    INT NOT NULL,
    unit_price  DECIMAL(12,2) NOT NULL,
    discount    DECIMAL(5,2) NOT NULL DEFAULT 0,
    line_total  DECIMAL(14,2) AS (CAST(quantity * unit_price * (1 - discount / 100) AS DECIMAL(14,2))) STORED,
    PRIMARY KEY (order_id, line_no),
    CONSTRAINT fk_items_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE,
    CONSTRAINT fk_items_product FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT ck_items_qty CHECK (quantity > 0),
    INDEX ix_items_product (product_id)
);

CREATE TABLE orders_audit (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    order_id    INT NOT NULL,
    old_status  VARCHAR(12) NULL,
    new_status  VARCHAR(12) NULL,
    changed_at  DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
);

INSERT INTO customers (full_name, email, phone, status, country, birth_date) VALUES
('Budi Santoso', 'budi@example.com', '+62 812-1111-2222', 'active', 'ID', '1990-05-14'),
('Siti Rahmawati', 'siti@example.com', '+62 813-3333-4444', 'active', 'ID', '1988-11-02'),
('Agus Wijaya', 'agus@example.com', NULL, 'inactive', 'ID', NULL),
('Dewi Lestari', 'dewi@example.com', '+62 815-5555-6666', 'banned', 'ID', '1995-01-30'),
('John O''Connor', 'john.oconnor@example.com', '+1 555 0100', 'active', 'US', '1979-07-04'),
('Ñandú García', 'nandu@example.es', NULL, 'active', 'ES', '2001-12-24'),
('山田 太郎', 'yamada@example.jp', '+81 90-1234-5678', 'active', 'JP', '1985-03-15'),
('Customer without orders', 'lonely@example.com', NULL, 'active', 'ID', NULL);

INSERT INTO products (sku, name, category, price, stock, is_active, attrs) VALUES
('LAP-001', 'Laptop GarudaTech Pro 14', 'Laptop', 18500000, 12, 1, '{"tags":["new","pro"],"ram_gb":16}'),
('LAP-002', 'Laptop GarudaTech Air 13', 'Laptop', 12999000, 0, 1, '{"tags":["sale"],"ram_gb":8}'),
('TAB-001', 'Tablet GarudaTech Plus1 64GB Silver', 'Tablet', 4250000, 30, 1, NULL),
('PHN-001', 'Ponsel Nusantara X', 'Phone', 3999000, 55, 1, '{"tags":["new"],"colors":["black","blue"]}'),
('PHN-002', 'Ponsel Nusantara X Mini', 'Phone', 2999000, 3, 1, '{"tags":[]}'),
('ACC-001', 'Mouse Nirkabel', 'Accessory', 149000, 200, 1, NULL),
('ACC-002', 'Keyboard Mekanik "Kopi"', 'Accessory', 899000, 18, 1, '{"tags":["sale","bundle"]}'),
('ACC-003', 'Kabel USB-C 2m', 'Accessory', 79000, 500, 1, NULL),
('OLD-001', 'Produk lama (discontinued)', NULL, 0, 0, 0, NULL),
('MON-001', 'Monitor 27" 4K', 'Monitor', 5499000, 7, 1, '{"tags":["pro"],"size_in":27}');

INSERT INTO orders (order_no, customer_id, status, ordered_at, notes)
WITH RECURSIVE s (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 60)
SELECT CONCAT('ORD-', LPAD(n, 5, '0')),
       (n % 7) + 1,
       ELT((n % 4) + 1, 'pending', 'paid', 'shipped', 'cancelled'),
       TIMESTAMP '2026-11-20 10:00:00' - INTERVAL n * 13 HOUR,
       CASE WHEN n % 5 = 0 THEN 'Kirim sebelum jam 5 sore.\nHubungi dulu.' END
FROM s;

INSERT INTO order_items (order_id, line_no, product_id, quantity, unit_price, discount)
SELECT o.id, l.n, p.id, ((o.id + l.n) % 4) + 1, p.price, CASE WHEN l.n = 2 THEN 10 ELSE 0 END
FROM orders AS o
JOIN (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3) AS l ON l.n <= (o.id % 3) + 1
JOIN products AS p ON p.id = ((o.id * 3 + l.n) % 10) + 1;

DELIMITER $$
CREATE TRIGGER trg_orders_status AFTER UPDATE ON orders FOR EACH ROW
BEGIN
    IF NOT (OLD.status <=> NEW.status) THEN
        INSERT INTO orders_audit (order_id, old_status, new_status) VALUES (NEW.id, OLD.status, NEW.status);
    END IF;
END$$

CREATE TRIGGER trg_products_sku_upper BEFORE INSERT ON products FOR EACH ROW
SET NEW.sku = UPPER(NEW.sku)$$
DELIMITER ;

UPDATE orders SET status = 'paid' WHERE id = 4;
UPDATE orders SET status = 'cancelled' WHERE id = 8;

/* Self-reference + SET NULL */
CREATE TABLE departments (
    id      INT PRIMARY KEY,
    name    VARCHAR(80) NOT NULL,
    CONSTRAINT uq_departments_name UNIQUE (name)
);
CREATE TABLE employees (
    id              INT PRIMARY KEY,
    name            VARCHAR(100) NOT NULL,
    manager_id      INT NULL,
    department_id   INT NULL,
    hired_on        DATE NOT NULL,
    salary_grade    TINYINT UNSIGNED NULL,
    CONSTRAINT fk_employees_manager FOREIGN KEY (manager_id) REFERENCES employees (id),
    CONSTRAINT fk_employees_department FOREIGN KEY (department_id) REFERENCES departments (id) ON DELETE SET NULL,
    CONSTRAINT ck_employees_grade CHECK (salary_grade BETWEEN 1 AND 10)
);
INSERT INTO departments VALUES (1, 'Engineering'), (2, 'Finance'), (3, 'Empty department');
INSERT INTO employees VALUES
(1, 'CEO', NULL, NULL, '2015-01-01', 10),
(2, 'CTO', 1, 1, '2016-03-01', 9),
(3, 'Engineer A', 2, 1, '2020-06-15', 5),
(4, 'Engineer B', 2, 1, '2021-09-01', 4),
(5, 'Finance Lead', 1, 2, '2018-02-12', 7),
(6, 'Intern', 3, NULL, '2026-07-01', NULL);

/* ============================== 3. Read-only and odd cases ============================== */

CREATE TABLE no_primary_key (code VARCHAR(10), amount INT, note VARCHAR(50));
INSERT INTO no_primary_key VALUES ('A', 1, 'dup'), ('A', 1, 'dup'), ('B', NULL, NULL);

CREATE TABLE unique_no_pk (code VARCHAR(10) NOT NULL, value INT, CONSTRAINT uq_unique_no_pk UNIQUE (code));
INSERT INTO unique_no_pk VALUES ('x', 1), ('y', 2);

CREATE TABLE empty_table (id INT PRIMARY KEY, name VARCHAR(50) NOT NULL, created_at DATETIME DEFAULT CURRENT_TIMESTAMP);

CREATE TABLE uuid_bin_pk (id BINARY(16) NOT NULL DEFAULT (UUID_TO_BIN(UUID(), 1)) PRIMARY KEY, name VARCHAR(50));
INSERT INTO uuid_bin_pk (name) VALUES ('first'), ('second'), ('third');

CREATE TABLE uuid_char_pk (id CHAR(36) NOT NULL DEFAULT (UUID()) PRIMARY KEY, name VARCHAR(50), meta JSON DEFAULT (JSON_OBJECT()));
INSERT INTO uuid_char_pk (name) VALUES ('first'), ('second');

CREATE TABLE auto_start (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50)) AUTO_INCREMENT = 1000;
INSERT INTO auto_start (name) VALUES ('a'), ('b'), ('c');

CREATE TABLE invoices (
    invoice_no  INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    order_id    INT NULL,
    total       DECIMAL(19,4) NOT NULL,
    CONSTRAINT fk_invoices_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT ON UPDATE NO ACTION
) AUTO_INCREMENT = 5000;
INSERT INTO invoices (order_id, total) SELECT id, 100000 * id FROM orders ORDER BY id LIMIT 10;

CREATE TABLE country_rates (
    country     CHAR(2) NOT NULL,
    valid_from  DATE NOT NULL,
    rate        DECIMAL(5,2) NOT NULL,
    PRIMARY KEY (country, valid_from)
);
INSERT INTO country_rates VALUES ('ID', '2025-01-01', 11.00), ('ID', '2026-01-01', 12.00), ('US', '2026-01-01', 0.00);

CREATE TABLE protected_rows (id INT PRIMARY KEY, name VARCHAR(50) NOT NULL);
INSERT INTO protected_rows VALUES (1, 'cannot delete me'), (2, 'nor me');
CREATE TRIGGER trg_protected_rows_delete BEFORE DELETE ON protected_rows FOR EACH ROW
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Rows in protected_rows cannot be deleted.';

CREATE TABLE collation_test (
    id      INT AUTO_INCREMENT PRIMARY KEY,
    ai_ci   VARCHAR(20) COLLATE utf8mb4_0900_ai_ci,
    as_cs   VARCHAR(20) COLLATE utf8mb4_0900_as_cs,
    bin     VARCHAR(20) COLLATE utf8mb4_bin,
    general VARCHAR(20) COLLATE utf8mb4_general_ci,
    latin1  VARCHAR(20) CHARACTER SET latin1 COLLATE latin1_swedish_ci,
    ascii   VARCHAR(20) CHARACTER SET ascii COLLATE ascii_bin
);
INSERT INTO collation_test (ai_ci, as_cs, bin, general, latin1, ascii) VALUES
('apple', 'apple', 'apple', 'apple', 'café', 'apple'),
('Apple', 'Apple', 'Apple', 'Apple', 'naïve', 'Apple'),
('APPLE', 'APPLE', 'APPLE', 'APPLE', 'Ünïcödé', 'APPLE'),
('äpple', 'äpple', 'äpple', 'äpple', 'äpple', 'apple2'),
('😀 emoji', '😀 emoji', '😀 emoji', '😀 emoji', 'no emoji', 'ascii'),
('_under', '_under', '_under', '_under', '_under', '_under');

CREATE TABLE sort_cases (
    id          INT PRIMARY KEY,
    n           INT NULL,
    txt         VARCHAR(20) NULL,
    num_as_text VARCHAR(10) NULL,
    amount      DECIMAL(10,2) NULL,
    happened    DATETIME NULL,
    flag        TINYINT(1) NULL
);
INSERT INTO sort_cases VALUES
(1, 10, 'banana', '10', -5.50, '2026-01-02 10:00', 1),
(2, NULL, 'Apple', '9', 100.00, NULL, 0),
(3, -3, 'apple', '100', 0.00, '2025-12-31 23:59', NULL),
(4, 10, NULL, NULL, NULL, '2026-01-02 10:00', 1),
(5, 0, 'Äpfel', '-1', -5.50, '1999-01-01 00:00', 0),
(6, 2147483647, 'zebra', '2', 99999999.99, '2030-06-01 08:00', 1),
(7, NULL, '', '', NULL, NULL, NULL);

/* Odd names: spaces, reserved words, backticks, unicode, leading digits */
CREATE TABLE `table with spaces` (
    `id`                    INT PRIMARY KEY,
    `first name`            VARCHAR(50),
    `select`                INT,
    `order`                 INT,
    `Column``WithBacktick`  VARCHAR(20),
    `kolom_ünïcödé`         VARCHAR(20),
    `123startsWithDigit`    INT,
    `MixedCase`             INT,
    `has.dot`               INT,
    `has"quote`             INT,
    `has'apostrophe`        INT
);
INSERT INTO `table with spaces` VALUES
(1, 'Ana', 1, 2, '``', 'ünï', 123, 1, 1, 1, 1),
(2, 'Bo', NULL, NULL, 'x', NULL, NULL, NULL, NULL, NULL, NULL);

CREATE TABLE `order` (id INT PRIMARY KEY, `group` VARCHAR(20), `user` VARCHAR(20), `key` VARCHAR(20));
INSERT INTO `order` VALUES (1, 'g1', 'u1', 'k1'), (2, 'g2', 'u2', 'k2');

CREATE TABLE `Ñandú 表` (id INT PRIMARY KEY, `名前` VARCHAR(20));
INSERT INTO `Ñandú 表` VALUES (1, 'テスト'), (2, 'ñandú');

CREATE TABLE `MixedCaseTable` (Id INT PRIMARY KEY, `Value` VARCHAR(20));
INSERT INTO `MixedCaseTable` VALUES (1, 'case');

/* ============================== 4. Engines, partitions, spatial ============================== */

CREATE TABLE myisam_notes (
    id      INT AUTO_INCREMENT PRIMARY KEY,
    title   VARCHAR(100) NOT NULL,
    body    TEXT,
    FULLTEXT INDEX ft_myisam_notes (title, body)
) ENGINE = MyISAM;
INSERT INTO myisam_notes (title, body) VALUES ('Catatan', 'Tabel MyISAM tanpa transaksi'), ('Second', 'Rollback cannot undo this');

CREATE TABLE memory_cache (k VARCHAR(50) PRIMARY KEY, v VARCHAR(200), expires_at DATETIME) ENGINE = MEMORY;
INSERT INTO memory_cache VALUES ('session:1', 'abc', '2026-12-01 00:00'), ('session:2', 'def', NULL);

CREATE TABLE events_log (
    id          BIGINT AUTO_INCREMENT,
    happened    DATE NOT NULL,
    level       ENUM('debug', 'info', 'warn', 'error') NOT NULL DEFAULT 'info',
    message     VARCHAR(200) NOT NULL,
    PRIMARY KEY (id, happened)
)
PARTITION BY RANGE (YEAR(happened)) (
    PARTITION p2024 VALUES LESS THAN (2025),
    PARTITION p2025 VALUES LESS THAN (2026),
    PARTITION p2026 VALUES LESS THAN (2027),
    PARTITION pmax  VALUES LESS THAN MAXVALUE
);
INSERT INTO events_log (happened, level, message)
WITH RECURSIVE s (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 1000)
SELECT DATE '2027-06-30' - INTERVAL n DAY, ELT((n % 4) + 1, 'debug', 'info', 'warn', 'error'), CONCAT('event ', n)
FROM s;

CREATE TABLE places (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    name        VARCHAR(100) NOT NULL,
    description TEXT,
    location    POINT NOT NULL SRID 4326,
    area        POLYGON NULL,
    SPATIAL INDEX sx_places_location (location),
    FULLTEXT INDEX ft_places (name, description)
);
INSERT INTO places (name, description, location, area) VALUES
('Monas', 'Monumen Nasional, Jakarta', ST_GeomFromText('POINT(106.8272 -6.1754)', 4326, 'axis-order=long-lat'),
 ST_GeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))')),
('Gedung Sate', 'Bandung', ST_GeomFromText('POINT(107.6186 -6.9025)', 4326, 'axis-order=long-lat'), NULL),
('Null Island', 'Lat 0, long 0', ST_GeomFromText('POINT(0 0)', 4326), NULL);

/* ============================== 5. Volume ============================== */

CREATE TABLE big_table (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    uuid        CHAR(36) NOT NULL DEFAULT (UUID()),
    category    VARCHAR(10) NULL,
    amount      DECIMAL(12,2) NOT NULL,
    created_at  DATETIME NOT NULL,
    note        VARCHAR(200) NULL,
    flag        TINYINT(1) NOT NULL,
    INDEX ix_big_table_category (category, created_at)
);
INSERT INTO big_table (category, amount, created_at, note, flag)
WITH RECURSIVE s (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 200000)
SELECT ELT((n % 5) + 1, 'alpha', 'beta', 'gamma', 'delta', NULL),
       ((n * 7919) % 1000000) / 100,
       TIMESTAMP '2026-11-22 00:00:00' - INTERVAL n MINUTE,
       CASE WHEN n % 10 = 0 THEN NULL ELSE CONCAT('Baris ', n, ' · catatan') END,
       n % 2
FROM s;

WITH RECURSIVE s (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 150)
SELECT GROUP_CONCAT(CONCAT('col_', LPAD(n, 3, '0'), ' VARCHAR(50) NULL') ORDER BY n SEPARATOR ', '),
       GROUP_CONCAT(CONCAT('col_', LPAD(n, 3, '0')) ORDER BY n SEPARATOR ', '),
       GROUP_CONCAT(CONCAT('CONCAT(''r'', r.n, ''_c', n, ''')') ORDER BY n SEPARATOR ', ')
INTO @defs, @names, @vals
FROM s;
SET @sql = CONCAT('CREATE TABLE wide_table (id INT AUTO_INCREMENT PRIMARY KEY, ', @defs, ')');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = CONCAT('INSERT INTO wide_table (', @names, ') WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 50) SELECT ', @vals, ' FROM r');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

/* ============================== 6. Views and programmability ============================== */

CREATE VIEW v_order_summary AS
SELECT o.id, o.order_no, c.full_name AS customer, o.status, o.ordered_at,
       COUNT(i.line_no) AS line_count, SUM(i.line_total) AS total
FROM orders AS o
JOIN customers AS c ON c.id = o.customer_id
LEFT JOIN order_items AS i ON i.order_id = o.id
GROUP BY o.id, o.order_no, c.full_name, o.status, o.ordered_at;

CREATE VIEW v_product_sales AS
SELECT i.product_id, p.name, COUNT(*) AS line_count, SUM(i.quantity) AS units
FROM order_items AS i
JOIN products AS p ON p.id = i.product_id
GROUP BY i.product_id, p.name;

CREATE ALGORITHM = MERGE VIEW v_active_customers AS
SELECT id, full_name, email, status FROM customers WHERE status = 'active'
WITH CASCADED CHECK OPTION;

CREATE VIEW v_types_preview AS
SELECT id, label, c_int, c_varchar, c_datetime, c_json FROM all_types;

DELIMITER $$
CREATE FUNCTION fn_rupiah (amount DECIMAL(18,2)) RETURNS VARCHAR(40)
DETERMINISTIC NO SQL
BEGIN
    RETURN CONCAT('Rp ', FORMAT(amount, 0, 'id_ID'));
END$$

CREATE FUNCTION fn_order_total (p_order_id INT) RETURNS DECIMAL(14,2)
READS SQL DATA
BEGIN
    RETURN (SELECT COALESCE(SUM(line_total), 0) FROM order_items WHERE order_id = p_order_id);
END$$

CREATE PROCEDURE usp_customer_orders (IN p_customer_id INT, OUT p_order_count INT)
READS SQL DATA
BEGIN
    SELECT * FROM v_order_summary WHERE id IN (SELECT id FROM orders WHERE customer_id = p_customer_id);
    SELECT COUNT(*) INTO p_order_count FROM orders WHERE customer_id = p_customer_id;
END$$

CREATE PROCEDURE usp_multi_results ()
BEGIN
    SELECT 'first message' AS message;
    SELECT * FROM customers LIMIT 5;
    SELECT * FROM products LIMIT 5;
    SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT = 'a warning from usp_multi_results';
    SELECT 1 AS one, 'two' AS two;
END$$

CREATE PROCEDURE usp_slow (IN p_seconds INT)
BEGIN
    DO SLEEP(p_seconds);
    SELECT 'done' AS result;
END$$

CREATE PROCEDURE usp_fail ()
BEGIN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Intentional failure from usp_fail', MYSQL_ERRNO = 50000;
END$$
DELIMITER ;

CREATE EVENT ev_cleanup_audit ON SCHEDULE EVERY 1 DAY DISABLE
COMMENT 'Disabled on purpose'
DO DELETE FROM orders_audit WHERE changed_at < NOW() - INTERVAL 90 DAY;

SELECT 'qorra_playground ready' AS status;
