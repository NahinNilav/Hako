-- Hako demonstration schema. The canonical implementation is
-- Initialize() in src/WarehouseLite.Core/WarehouseService.vb.
PRAGMA foreign_keys = ON;
CREATE TABLE Products (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  sku TEXT NOT NULL UNIQUE COLLATE NOCASE,
  name TEXT NOT NULL,
  category TEXT NOT NULL,
  unit_price_cents INTEGER NOT NULL CHECK(unit_price_cents >= 0),
  reorder_level INTEGER NOT NULL CHECK(reorder_level >= 0),
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
);
CREATE TABLE Locations (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  code TEXT NOT NULL UNIQUE COLLATE NOCASE,
  zone TEXT NOT NULL,
  description TEXT NOT NULL,
  capacity_units INTEGER NOT NULL CHECK(capacity_units > 0)
);
CREATE TABLE Inventory (
  product_id INTEGER NOT NULL REFERENCES Products(id),
  location_id INTEGER NOT NULL REFERENCES Locations(id),
  quantity INTEGER NOT NULL DEFAULT 0 CHECK(quantity >= 0),
  PRIMARY KEY(product_id,location_id)
);
CREATE TABLE Movements (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  movement_type TEXT NOT NULL CHECK(movement_type IN ('RECEIVE','DISPATCH','TRANSFER')),
  product_id INTEGER NOT NULL REFERENCES Products(id),
  quantity INTEGER NOT NULL CHECK(quantity > 0),
  from_location_id INTEGER REFERENCES Locations(id),
  to_location_id INTEGER REFERENCES Locations(id),
  reference TEXT NOT NULL,
  note TEXT NOT NULL DEFAULT '',
  created_utc TEXT NOT NULL
);
CREATE INDEX idx_movements_date ON Movements(created_utc DESC);
CREATE INDEX idx_movements_product ON Movements(product_id);
CREATE INDEX idx_inventory_location ON Inventory(location_id);
