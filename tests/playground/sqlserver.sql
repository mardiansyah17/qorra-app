/*
  Qorra playground for SQL Server 2022+ (json / vector need 2025): every column type, edge values, constraints,
  odd names, big / wide tables, temporal, graph, columnstore, views and programmability.
  Re-runnable: drops and recreates the qorra_playground database (only that one).

  sqlcmd -S 127.0.0.1,59088 -U <user> -P <password> -C -I -b -f 65001 -i tests/playground/sqlserver.sql
*/
USE master;
GO
IF DB_ID(N'qorra_playground') IS NOT NULL
BEGIN
    ALTER DATABASE qorra_playground SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE qorra_playground;
END
GO
CREATE DATABASE qorra_playground;
GO
USE qorra_playground;
GO

CREATE SCHEMA sales;
GO
CREATE SCHEMA hr;
GO
CREATE SCHEMA [weird schema];
GO

/* ============================== 1. Every column type ============================== */

CREATE TABLE dbo.all_types (
    id                  int IDENTITY(1,1) CONSTRAINT pk_all_types PRIMARY KEY,
    label               nvarchar(40) NOT NULL,
    c_bigint            bigint,
    c_int               int,
    c_smallint          smallint,
    c_tinyint           tinyint,
    c_bit               bit,
    c_decimal           decimal(38,10),
    c_numeric           numeric(10,2),
    c_money             money,
    c_smallmoney        smallmoney,
    c_float             float,
    c_real              real,
    c_date              date,
    c_time              time(7),
    c_datetime          datetime,
    c_datetime2         datetime2(7),
    c_datetimeoffset    datetimeoffset(7),
    c_smalldatetime     smalldatetime,
    c_char              char(10),
    c_varchar           varchar(100),
    c_varchar_max       varchar(max),
    c_text              text,
    c_nchar             nchar(10),
    c_nvarchar          nvarchar(100),
    c_nvarchar_max      nvarchar(max),
    c_ntext             ntext,
    c_binary            binary(8),
    c_varbinary         varbinary(64),
    c_varbinary_max     varbinary(max),
    c_image             image,
    c_uniqueidentifier  uniqueidentifier,
    c_xml               xml,
    c_sql_variant       sql_variant,
    c_hierarchyid       hierarchyid,
    c_geometry          geometry,
    c_geography         geography,
    c_json              json,
    c_vector            vector(3),
    c_rowversion        rowversion,
    c_computed          AS (CAST(c_int AS bigint) * 2) PERSISTED,
    c_computed_virtual  AS (UPPER(c_varchar))
);
GO

INSERT INTO dbo.all_types (label, c_bigint, c_int, c_smallint, c_tinyint, c_bit, c_decimal, c_numeric, c_money, c_smallmoney,
    c_float, c_real, c_date, c_time, c_datetime, c_datetime2, c_datetimeoffset, c_smalldatetime,
    c_char, c_varchar, c_varchar_max, c_text, c_nchar, c_nvarchar, c_nvarchar_max, c_ntext,
    c_binary, c_varbinary, c_varbinary_max, c_image, c_uniqueidentifier, c_xml, c_sql_variant,
    c_hierarchyid, c_geometry, c_geography, c_json, c_vector)
VALUES
(N'typical', 1234567890123, 42, 123, 200, 1, 12345.6789012345, 99.95, 15000.50, 250.25,
 3.14159265358979, 2.71828, '2026-11-22', '13:45:30.1234567', '2026-11-22 13:45:30.123', '2026-11-22 13:45:30.1234567',
 '2026-11-22 13:45:30.1234567 +07:00', '2026-11-22 13:45',
 'abc', 'Hello world', 'varchar max value', 'legacy text', N'nchar', N'Halo dunia', N'nvarchar max value', N'legacy ntext',
 0x0102030405060708, 0xDEADBEEF, 0xCAFEBABE, 0x89504E470D0A1A0A, '6F9619FF-8B86-D011-B42D-00C04FC964FF',
 N'<root><item id="1">a</item><item id="2">b</item></root>', CAST(42 AS sql_variant),
 hierarchyid::Parse('/1/2/'), geometry::STGeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))', 0),
 geography::Point(-6.2088, 106.8456, 4326), N'{"name":"Qorra","tags":["a","b"],"n":1.5,"nested":{"ok":true}}', '[0.1, 0.2, 0.3]'),
(N'minimum', -9223372036854775808, -2147483648, -32768, 0, 0, -9999999999999999999999999999.9999999999, -99999999.99,
 -922337203685477.5808, -214748.3648, -1.79E+308, -3.40E+38, '0001-01-01', '00:00:00', '1753-01-01 00:00:00', '0001-01-01 00:00:00',
 '0001-01-01 00:00:00 -14:00', '1900-01-01 00:00',
 '', '', '', '', N'', N'', N'', N'',
 0x0000000000000000, 0x, 0x, 0x, '00000000-0000-0000-0000-000000000000', N'<empty/>', CAST(N'' AS sql_variant),
 hierarchyid::GetRoot(), geometry::STGeomFromText('POINT(0 0)', 0), geography::Point(-90, -180, 4326), N'{}', '[-1, -1, -1]'),
(N'maximum', 9223372036854775807, 2147483647, 32767, 255, 1, 9999999999999999999999999999.9999999999, 99999999.99,
 922337203685477.5807, 214748.3647, 1.79E+308, 3.40E+38, '9999-12-31', '23:59:59.9999999', '9999-12-31 23:59:59.997', '9999-12-31 23:59:59.9999999',
 '9999-12-31 23:59:59.9999999 +14:00', '2079-06-06 23:59',
 'ZZZZZZZZZZ', REPLICATE('x', 100), REPLICATE(CAST('long varchar ' AS varchar(max)), 20000), REPLICATE('t', 8000),
 N'ZZZZZZZZZZ', REPLICATE(N'y', 100), REPLICATE(CAST(N'Lorem ipsum dolor sit amet. ' AS nvarchar(max)), 10000), REPLICATE(N'n', 4000),
 0xFFFFFFFFFFFFFFFF, CAST(REPLICATE('F', 64) AS varbinary(64)),
 CAST(REPLICATE(CAST('A' AS varchar(max)), 100000) AS varbinary(max)), CAST(REPLICATE(CAST('B' AS varchar(max)), 70000) AS varbinary(max)),
 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF', N'<big>' + REPLICATE(CAST(N'<n>x</n>' AS nvarchar(max)), 2000) + N'</big>',
 CAST(CAST('2026-01-01' AS datetime2) AS sql_variant),
 hierarchyid::Parse('/1/2/3/4/5/6/7/8/9/10/'), geometry::STGeomFromText('LINESTRING(0 0, 100 100, 200 0)', 0),
 geography::Point(90, 180, 4326), N'[' + REPLICATE(CAST(N'{"k":1},' AS nvarchar(max)), 500) + N'{"k":2}]', '[1e10, -1e10, 0]'),
(N'all null', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
 NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
(N'tricky text', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '2000-02-29', '12:00', '2000-02-29 12:00', '2000-02-29 12:00',
 '2000-02-29 12:00 -05:30', '2000-02-29 12:00',
 'NULL', 'it''s "quoted" `back` [bracket]', 'line1' + CHAR(13) + CHAR(10) + 'line2' + CHAR(10) + 'line3', CHAR(9) + 'tab' + CHAR(9) + 'separated',
 N'  pad  ', N'😀🚀 العربية 日本語 Ελληνικά Tiếng Việt', N'<script>alert(1)</script>''; DROP TABLE x; --', N'   leading and trailing   ',
 0x00, 0x00FF00FF, 0x0D0A, 0x00, NEWID(), N'<a attr="&lt;&amp;&gt;">text &amp; more</a>', CAST(N'variant text 😀' AS sql_variant),
 hierarchyid::Parse('/1/'), geometry::STGeomFromText('MULTIPOINT((1 1), (2 2))', 0),
 geography::STGeomFromText('LINESTRING(106.8 -6.2, 107.6 -6.9)', 4326), N'{"quote":"\"","unicode":"😀","newline":"a\nb"}', '[0, 0, 0]'),
(N'precision', 1, 1, 1, 1, 1, 0.0000000001, 0.01, 0.0001, 0.0001, 2.2250738585072014E-308, 1.17549435E-38, '1970-01-01', '00:00:00.0000001',
 '1970-01-01 00:00:00.003', '1970-01-01 00:00:00.0000001', '1970-01-01 00:00:00.0000001 +00:00', '1970-01-01 00:00',
 '1', '0.1', '1e10', '-0', N'1', N'0.30000000000000004', N'NaN', N'Infinity',
 0x01, 0x01, 0x01, 0x01, NEWID(), N'<n>1</n>', CAST(CAST(0.1 AS float) AS sql_variant),
 hierarchyid::Parse('/0.5/'), geometry::STGeomFromText('POINT(0.1 0.2)', 0), geography::Point(0, 0, 4326), N'[]', '[0.1, 0.2, 0.30000001]');
GO

/* ============================== 2. Relations and constraints ============================== */

CREATE TABLE sales.customers (
    id          int IDENTITY(1,1) CONSTRAINT pk_customers PRIMARY KEY,
    full_name   nvarchar(120) NOT NULL,
    email       varchar(200) NOT NULL CONSTRAINT uq_customers_email UNIQUE,
    phone       varchar(20) NULL,
    status      varchar(10) NOT NULL CONSTRAINT df_customers_status DEFAULT ('active')
                CONSTRAINT ck_customers_status CHECK (status IN ('active', 'inactive', 'banned')),
    country     char(2) NOT NULL CONSTRAINT df_customers_country DEFAULT ('ID'),
    birth_date  date NULL CONSTRAINT ck_customers_birth CHECK (birth_date >= '1900-01-01'),
    created_at  datetime2(3) NOT NULL CONSTRAINT df_customers_created DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE sales.products (
    id          int IDENTITY(1,1) CONSTRAINT pk_products PRIMARY KEY,
    sku         varchar(30) NOT NULL CONSTRAINT uq_products_sku UNIQUE,
    name        nvarchar(150) NOT NULL,
    category    nvarchar(50) NULL,
    price       decimal(12,2) NOT NULL CONSTRAINT ck_products_price CHECK (price >= 0),
    stock       int NOT NULL CONSTRAINT df_products_stock DEFAULT (0),
    is_active   bit NOT NULL CONSTRAINT df_products_active DEFAULT (1),
    CONSTRAINT ck_products_stock_active CHECK (stock >= 0 OR is_active = 0)
);

CREATE TABLE sales.orders (
    id          int IDENTITY(1,1) CONSTRAINT pk_orders PRIMARY KEY,
    order_no    varchar(20) NOT NULL CONSTRAINT uq_orders_no UNIQUE,
    customer_id int NOT NULL CONSTRAINT fk_orders_customer REFERENCES sales.customers (id) ON DELETE CASCADE ON UPDATE CASCADE,
    status      varchar(12) NOT NULL CONSTRAINT df_orders_status DEFAULT ('pending')
                CONSTRAINT ck_orders_status CHECK (status IN ('pending', 'paid', 'shipped', 'cancelled')),
    ordered_at  datetime2(0) NOT NULL,
    updated_at  datetime2(3) NULL,
    notes       nvarchar(max) NULL
);

CREATE TABLE sales.order_items (
    order_id    int NOT NULL CONSTRAINT fk_items_order REFERENCES sales.orders (id) ON DELETE CASCADE,
    line_no     smallint NOT NULL,
    product_id  int NOT NULL CONSTRAINT fk_items_product REFERENCES sales.products (id),
    quantity    int NOT NULL CONSTRAINT ck_items_qty CHECK (quantity > 0),
    unit_price  decimal(12,2) NOT NULL,
    discount    decimal(5,2) NOT NULL CONSTRAINT df_items_discount DEFAULT (0),
    line_total  AS (CAST(quantity * unit_price * (1 - discount / 100) AS decimal(14,2))) PERSISTED,
    CONSTRAINT pk_order_items PRIMARY KEY (order_id, line_no)
);

CREATE INDEX ix_orders_customer_date ON sales.orders (customer_id, ordered_at DESC) INCLUDE (status);
CREATE INDEX ix_orders_open ON sales.orders (ordered_at) WHERE status IN ('pending', 'paid');
CREATE INDEX ix_products_category ON sales.products (category, price DESC);
CREATE INDEX ix_items_product ON sales.order_items (product_id);
GO

EXEC sys.sp_addextendedproperty N'MS_Description', N'Pelanggan toko', N'SCHEMA', N'sales', N'TABLE', N'customers';
EXEC sys.sp_addextendedproperty N'MS_Description', N'Email unik, dipakai untuk login', N'SCHEMA', N'sales', N'TABLE', N'customers', N'COLUMN', N'email';
EXEC sys.sp_addextendedproperty N'MS_Description', N'Harga dalam rupiah', N'SCHEMA', N'sales', N'TABLE', N'products', N'COLUMN', N'price';
GO

INSERT INTO sales.customers (full_name, email, phone, status, country, birth_date) VALUES
(N'Budi Santoso', 'budi@example.com', '+62 812-1111-2222', 'active', 'ID', '1990-05-14'),
(N'Siti Rahmawati', 'siti@example.com', '+62 813-3333-4444', 'active', 'ID', '1988-11-02'),
(N'Agus Wijaya', 'agus@example.com', NULL, 'inactive', 'ID', NULL),
(N'Dewi Lestari', 'dewi@example.com', '+62 815-5555-6666', 'banned', 'ID', '1995-01-30'),
(N'John O''Connor', 'john.oconnor@example.com', '+1 555 0100', 'active', 'US', '1979-07-04'),
(N'Ñandú García', 'nandu@example.es', NULL, 'active', 'ES', '2001-12-24'),
(N'山田 太郎', 'yamada@example.jp', '+81 90-1234-5678', 'active', 'JP', '1985-03-15'),
(N'Customer without orders', 'lonely@example.com', NULL, 'active', 'ID', NULL);

INSERT INTO sales.products (sku, name, category, price, stock, is_active) VALUES
('LAP-001', N'Laptop GarudaTech Pro 14', N'Laptop', 18500000, 12, 1),
('LAP-002', N'Laptop GarudaTech Air 13', N'Laptop', 12999000, 0, 1),
('TAB-001', N'Tablet GarudaTech Plus1 64GB Silver', N'Tablet', 4250000, 30, 1),
('PHN-001', N'Ponsel Nusantara X', N'Phone', 3999000, 55, 1),
('PHN-002', N'Ponsel Nusantara X Mini', N'Phone', 2999000, 3, 1),
('ACC-001', N'Mouse Nirkabel', N'Accessory', 149000, 200, 1),
('ACC-002', N'Keyboard Mekanik "Kopi"', N'Accessory', 899000, 18, 1),
('ACC-003', N'Kabel USB-C 2m', N'Accessory', 79000, 500, 1),
('OLD-001', N'Produk lama (discontinued)', NULL, 0, 0, 0),
('MON-001', N'Monitor 27" 4K', N'Monitor', 5499000, 7, 1);

INSERT INTO sales.orders (order_no, customer_id, status, ordered_at, notes)
SELECT CONCAT('ORD-', FORMAT(s.value, '00000')),
       (s.value % 7) + 1,
       CHOOSE((s.value % 4) + 1, 'pending', 'paid', 'shipped', 'cancelled'),
       DATEADD(hour, -s.value * 13, CAST('2026-11-20T10:00:00' AS datetime2(0))),
       CASE WHEN s.value % 5 = 0 THEN N'Kirim sebelum jam 5 sore.' + CHAR(10) + N'Hubungi dulu.' END
FROM GENERATE_SERIES(1, 60) AS s;

INSERT INTO sales.order_items (order_id, line_no, product_id, quantity, unit_price, discount)
SELECT o.id, l.value, p.id, ((o.id + l.value) % 4) + 1, p.price, CASE WHEN l.value = 2 THEN 10 ELSE 0 END
FROM sales.orders AS o
CROSS APPLY GENERATE_SERIES(1, (o.id % 3) + 1) AS l
JOIN sales.products AS p ON p.id = ((o.id * 3 + l.value) % 10) + 1;
GO

CREATE TRIGGER sales.trg_orders_touch ON sales.orders AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE o SET updated_at = SYSUTCDATETIME() FROM sales.orders AS o JOIN inserted AS i ON i.id = o.id;
END;
GO

/* Self-reference + SET NULL */
CREATE TABLE hr.departments (
    id      int CONSTRAINT pk_departments PRIMARY KEY,
    name    nvarchar(80) NOT NULL CONSTRAINT uq_departments_name UNIQUE
);
CREATE TABLE hr.employees (
    id              int CONSTRAINT pk_employees PRIMARY KEY,
    name            nvarchar(100) NOT NULL,
    manager_id      int NULL CONSTRAINT fk_employees_manager REFERENCES hr.employees (id),
    department_id   int NULL CONSTRAINT fk_employees_department REFERENCES hr.departments (id) ON DELETE SET NULL,
    hired_on        date NOT NULL,
    salary_grade    tinyint NULL CONSTRAINT ck_employees_grade CHECK (salary_grade BETWEEN 1 AND 10)
);
INSERT INTO hr.departments VALUES (1, N'Engineering'), (2, N'Finance'), (3, N'Empty department');
INSERT INTO hr.employees VALUES
(1, N'CEO', NULL, NULL, '2015-01-01', 10),
(2, N'CTO', 1, 1, '2016-03-01', 9),
(3, N'Engineer A', 2, 1, '2020-06-15', 5),
(4, N'Engineer B', 2, 1, '2021-09-01', 4),
(5, N'Finance Lead', 1, 2, '2018-02-12', 7),
(6, N'Intern', 3, NULL, '2026-07-01', NULL);
GO

/* System-versioned (temporal) with hidden period columns */
CREATE TABLE hr.salaries (
    employee_id int CONSTRAINT pk_salaries PRIMARY KEY CONSTRAINT fk_salaries_employee REFERENCES hr.employees (id),
    amount      decimal(12,2) NOT NULL,
    valid_from  datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    valid_to    datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = hr.salaries_history));
GO
INSERT INTO hr.salaries (employee_id, amount) VALUES (1, 90000000), (2, 70000000), (3, 25000000), (4, 22000000), (5, 40000000);
UPDATE hr.salaries SET amount = amount * 1.1 WHERE employee_id IN (3, 4);
UPDATE hr.salaries SET amount = amount + 1000000 WHERE employee_id = 3;
GO

/* Graph tables */
CREATE TABLE hr.person (id int CONSTRAINT pk_person PRIMARY KEY, name nvarchar(100) NOT NULL) AS NODE;
CREATE TABLE hr.knows (since date NULL) AS EDGE;
GO
INSERT INTO hr.person VALUES (1, N'Alice'), (2, N'Bob'), (3, N'Charlie');
INSERT INTO hr.knows ($from_id, $to_id, since)
SELECT a.$node_id, b.$node_id, '2020-01-01' FROM hr.person AS a, hr.person AS b WHERE a.id = 1 AND b.id IN (2, 3);
GO

/* ============================== 3. Read-only and odd cases ============================== */

CREATE TABLE dbo.no_primary_key (code varchar(10), amount int, note nvarchar(50));
INSERT INTO dbo.no_primary_key VALUES ('A', 1, N'dup'), ('A', 1, N'dup'), ('B', NULL, NULL);

CREATE TABLE dbo.unique_no_pk (code varchar(10) NOT NULL CONSTRAINT uq_unique_no_pk UNIQUE, value int);
INSERT INTO dbo.unique_no_pk VALUES ('x', 1), ('y', 2);

CREATE TABLE dbo.empty_table (id int CONSTRAINT pk_empty PRIMARY KEY, name nvarchar(50) NOT NULL, created_at datetime2 DEFAULT SYSUTCDATETIME());

CREATE TABLE dbo.guid_pk (id uniqueidentifier CONSTRAINT df_guid_pk DEFAULT NEWSEQUENTIALID() CONSTRAINT pk_guid_pk PRIMARY KEY, name nvarchar(50));
INSERT INTO dbo.guid_pk (name) VALUES (N'first'), (N'second'), (N'third');

CREATE TABLE dbo.identity_step (id int IDENTITY(1000, 10) CONSTRAINT pk_identity_step PRIMARY KEY, name nvarchar(50));
INSERT INTO dbo.identity_step (name) VALUES (N'a'), (N'b'), (N'c');

CREATE SEQUENCE dbo.seq_invoice AS int START WITH 5000 INCREMENT BY 1;
CREATE TABLE dbo.invoices (
    invoice_no  int CONSTRAINT df_invoice_no DEFAULT (NEXT VALUE FOR dbo.seq_invoice) CONSTRAINT pk_invoices PRIMARY KEY,
    order_id    int NULL CONSTRAINT fk_invoices_order REFERENCES sales.orders (id),
    total       money NOT NULL
);
INSERT INTO dbo.invoices (order_id, total) SELECT TOP (10) id, 100000 * id FROM sales.orders ORDER BY id;
GO

CREATE TABLE dbo.protected_rows (id int CONSTRAINT pk_protected PRIMARY KEY, name nvarchar(50) NOT NULL);
INSERT INTO dbo.protected_rows VALUES (1, N'cannot delete me'), (2, N'nor me');
GO
CREATE TRIGGER dbo.trg_protected_rows_delete ON dbo.protected_rows INSTEAD OF DELETE AS
    THROW 50001, N'Rows in dbo.protected_rows cannot be deleted.', 1;
GO

CREATE TYPE dbo.Phone FROM varchar(20) NOT NULL;
CREATE TYPE dbo.IdList AS TABLE (id int PRIMARY KEY);
GO
CREATE TABLE dbo.contacts (id int IDENTITY CONSTRAINT pk_contacts PRIMARY KEY, name nvarchar(50) NOT NULL, phone dbo.Phone);
INSERT INTO dbo.contacts (name, phone) VALUES (N'Kantor', '+62 21 555 0101'), (N'Gudang', '+62 21 555 0202');
GO

CREATE TABLE dbo.collation_test (
    id      int IDENTITY CONSTRAINT pk_collation_test PRIMARY KEY,
    ci      varchar(20) COLLATE Latin1_General_CI_AS,
    cs      varchar(20) COLLATE Latin1_General_CS_AS,
    bin     varchar(20) COLLATE Latin1_General_BIN2,
    utf8    varchar(40) COLLATE Latin1_General_100_CI_AS_SC_UTF8
);
INSERT INTO dbo.collation_test (ci, cs, bin, utf8) VALUES
('apple', 'apple', 'apple', 'café'), ('Apple', 'Apple', 'Apple', 'naïve'), ('APPLE', 'APPLE', 'APPLE', '😀 emoji'),
('banana', 'banana', 'banana', 'Ünïcödé'), ('_under', '_under', '_under', 'ascii');

CREATE TABLE dbo.sort_cases (
    id          int CONSTRAINT pk_sort_cases PRIMARY KEY,
    n           int NULL,
    txt         nvarchar(20) NULL,
    num_as_text varchar(10) NULL,
    amount      decimal(10,2) NULL,
    happened    datetime2(0) NULL,
    flag        bit NULL
);
INSERT INTO dbo.sort_cases VALUES
(1, 10, N'banana', '10', -5.50, '2026-01-02 10:00', 1),
(2, NULL, N'Apple', '9', 100.00, NULL, 0),
(3, -3, N'apple', '100', 0.00, '2025-12-31 23:59', NULL),
(4, 10, NULL, NULL, NULL, '2026-01-02 10:00', 1),
(5, 0, N'Äpfel', '-1', -5.50, '1999-01-01 00:00', 0),
(6, 2147483647, N'zebra', '2', 99999999.99, '2030-06-01 08:00', 1),
(7, NULL, N'', '', NULL, NULL, NULL);
GO

/* Odd names: spaces, reserved words, brackets, unicode, leading digits */
CREATE TABLE [weird schema].[table with spaces] (
    [id]                    int CONSTRAINT [pk table with spaces] PRIMARY KEY,
    [first name]            nvarchar(50),
    [select]                int,
    [order]                 int,
    [Column]]WithBracket]   nvarchar(20),
    [kolom_ünïcödé]         nvarchar(20),
    [123startsWithDigit]    int,
    [MixedCase]             int,
    [has.dot]               int,
    [has"quote]             int
);
INSERT INTO [weird schema].[table with spaces] VALUES
(1, N'Ana', 1, 2, N']]', N'ünï', 123, 1, 1, 1),
(2, N'Bo', NULL, NULL, N'x', NULL, NULL, NULL, NULL, NULL);

CREATE TABLE dbo.[order] (id int CONSTRAINT pk_order_reserved PRIMARY KEY, [group] nvarchar(20), [user] nvarchar(20));
INSERT INTO dbo.[order] VALUES (1, N'g1', N'u1'), (2, N'g2', N'u2');

CREATE TABLE dbo.[Ñandú 表] (id int CONSTRAINT pk_nandu PRIMARY KEY, [名前] nvarchar(20));
INSERT INTO dbo.[Ñandú 表] VALUES (1, N'テスト'), (2, N'ñandú');
GO

/* ============================== 4. Volume ============================== */

CREATE TABLE dbo.big_table (
    id          bigint IDENTITY CONSTRAINT pk_big_table PRIMARY KEY,
    uuid        uniqueidentifier NOT NULL CONSTRAINT df_big_uuid DEFAULT NEWID(),
    category    varchar(10) NULL,
    amount      decimal(12,2) NOT NULL,
    created_at  datetime2(0) NOT NULL,
    note        nvarchar(200) NULL,
    flag        bit NOT NULL
);
INSERT INTO dbo.big_table (category, amount, created_at, note, flag)
SELECT CHOOSE(s.value % 5 + 1, 'alpha', 'beta', 'gamma', 'delta', NULL),
       CAST((s.value * 7919) % 1000000 AS decimal(12,2)) / 100,
       DATEADD(minute, -s.value, CAST('2026-11-22T00:00:00' AS datetime2(0))),
       CASE WHEN s.value % 10 = 0 THEN NULL ELSE CONCAT(N'Baris ', s.value, N' · catatan') END,
       s.value % 2
FROM GENERATE_SERIES(1, 200000) AS s;
CREATE INDEX ix_big_table_category ON dbo.big_table (category, created_at);
GO

DECLARE @defs nvarchar(max), @names nvarchar(max), @vals nvarchar(max);
SELECT @defs  = STRING_AGG(CAST(CONCAT(N'col_', FORMAT(value, '000'), N' nvarchar(50) NULL') AS nvarchar(max)), N', '),
       @names = STRING_AGG(CAST(CONCAT(N'col_', FORMAT(value, '000')) AS nvarchar(max)), N', '),
       @vals  = STRING_AGG(CAST(CONCAT(N'CONCAT(N''r'', s.value, N''_c', value, N''')') AS nvarchar(max)), N', ')
FROM GENERATE_SERIES(1, 150);
EXEC (N'CREATE TABLE dbo.wide_table (id int IDENTITY CONSTRAINT pk_wide_table PRIMARY KEY, ' + @defs + N')');
EXEC (N'INSERT INTO dbo.wide_table (' + @names + N') SELECT ' + @vals + N' FROM GENERATE_SERIES(1, 50) AS s');
GO

CREATE TABLE dbo.cs_sales (sale_date date NOT NULL, region varchar(10) NOT NULL, qty int NOT NULL, revenue money NOT NULL);
CREATE CLUSTERED COLUMNSTORE INDEX cci_cs_sales ON dbo.cs_sales;
INSERT INTO dbo.cs_sales
SELECT DATEADD(day, -(s.value % 730), '2026-11-22'), CHOOSE(s.value % 4 + 1, 'Jawa', 'Sumatra', 'Bali', 'Papua'), s.value % 20 + 1, (s.value % 500) * 1000
FROM GENERATE_SERIES(1, 50000) AS s;

CREATE TABLE dbo.sparse_table (id int CONSTRAINT pk_sparse PRIMARY KEY, a int SPARSE NULL, b nvarchar(50) SPARSE NULL, c date SPARSE NULL);
INSERT INTO dbo.sparse_table (id, a) VALUES (1, 1);
INSERT INTO dbo.sparse_table (id, b) VALUES (2, N'only b');
INSERT INTO dbo.sparse_table (id) VALUES (3);
GO

/* ============================== 5. Views and programmability ============================== */

CREATE VIEW sales.v_order_summary AS
SELECT o.id, o.order_no, c.full_name AS customer, o.status, o.ordered_at,
       COUNT(i.line_no) AS lines, SUM(i.line_total) AS total
FROM sales.orders AS o
JOIN sales.customers AS c ON c.id = o.customer_id
LEFT JOIN sales.order_items AS i ON i.order_id = o.id
GROUP BY o.id, o.order_no, c.full_name, o.status, o.ordered_at;
GO

CREATE VIEW sales.v_product_sales WITH SCHEMABINDING AS
SELECT i.product_id, COUNT_BIG(*) AS line_count, SUM(i.quantity) AS units
FROM sales.order_items AS i
GROUP BY i.product_id;
GO
CREATE UNIQUE CLUSTERED INDEX ix_v_product_sales ON sales.v_product_sales (product_id);
GO

CREATE VIEW dbo.v_types_preview AS
SELECT id, label, c_int, c_nvarchar, c_datetime2, c_json FROM dbo.all_types;
GO

CREATE SYNONYM dbo.customers FOR sales.customers;
GO

CREATE FUNCTION dbo.fn_rupiah (@amount decimal(18,2)) RETURNS nvarchar(40) AS
BEGIN
    RETURN N'Rp ' + FORMAT(@amount, 'N0', 'id-ID');
END;
GO

CREATE FUNCTION sales.fn_orders_by_status (@status varchar(12)) RETURNS TABLE AS
RETURN SELECT id, order_no, customer_id, ordered_at FROM sales.orders WHERE status = @status;
GO

CREATE FUNCTION sales.fn_top_customers (@top int) RETURNS @result TABLE (customer_id int, orders int) AS
BEGIN
    INSERT INTO @result SELECT TOP (@top) customer_id, COUNT(*) FROM sales.orders GROUP BY customer_id ORDER BY COUNT(*) DESC;
    RETURN;
END;
GO

CREATE PROCEDURE sales.usp_customer_orders @customer_id int, @order_count int OUTPUT AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM sales.v_order_summary WHERE id IN (SELECT id FROM sales.orders WHERE customer_id = @customer_id);
    SELECT @order_count = COUNT(*) FROM sales.orders WHERE customer_id = @customer_id;
END;
GO

CREATE PROCEDURE dbo.usp_multi_results AS
BEGIN
    PRINT N'first message';
    SELECT TOP (5) * FROM sales.customers;
    SELECT TOP (5) * FROM sales.products;
    RAISERROR (N'a warning (severity 10)', 10, 1);
    SELECT 1 AS one, N'two' AS two;
END;
GO

CREATE PROCEDURE dbo.usp_slow @seconds int = 30 AS
BEGIN
    DECLARE @delay char(8) = CONVERT(char(8), DATEADD(second, @seconds, 0), 108);
    WAITFOR DELAY @delay;
    SELECT N'done' AS result;
END;
GO

CREATE PROCEDURE dbo.usp_fail AS
    THROW 50000, N'Intentional failure from dbo.usp_fail', 1;
GO

PRINT N'qorra_playground ready';
