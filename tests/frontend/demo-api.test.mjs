import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { DemoWarehouse } from '../../src/WarehouseLite.Web/wwwroot/assets/demo-api.js';

const seed = JSON.parse(readFileSync(new URL('../../src/WarehouseLite.Web/wwwroot/assets/seed.json', import.meta.url),'utf8'));
const fresh = () => new DemoWarehouse(seed,{persist:false,storage:null});
const initialStock = (db,sku) => db.productList().find(p=>p.sku===sku).stock;

test('seed contains 46 catalog items, 8 bins, historical transactions',()=>{
  const db=fresh();assert.equal(db.productList().length,46);assert.equal(db.locationList().length,8);
  assert.ok(db.movements().length>95);assert.ok(db.dashboard().lowStock>0);
});
test('receive increases stock and records an auditable receipt',async()=>{
  const db=fresh(),before=initialStock(db,'SKU-1001');
  const result=await db.request('/api/movements/receive',{method:'POST',body:{sku:'SKU-1001',toLocation:'A-01',quantity:11,reference:'PO-TEST'}});
  assert.ok(result.id);assert.equal(initialStock(db,'SKU-1001'),before+11);
  assert.equal(db.movements()[0].reference,'PO-TEST');
});
test('dispatch cannot create negative inventory or an audit entry',()=>{
  const db=fresh(), before=initialStock(db,'SKU-1001'), count=db.movements().length;
  assert.throws(()=>db.mutateMove('DISPATCH',{sku:'SKU-1001',fromLocation:'A-01',quantity:9999,reference:'SO-BAD'}),/Insufficient stock/);
  assert.equal(before,initialStock(db,'SKU-1001'));assert.equal(count,db.movements().length);
});
test('transfer keeps warehouse-wide stock unchanged and changes both locations',()=>{
  const db=fresh(); const record=db.inventoryList().find(x=>x.quantity>=10);
  const target=db.locationList().find(l=>l.code!==record.location).code;
  const before=initialStock(db,record.sku),source=db.stock(record.sku,record.location),destination=db.stock(record.sku,target);
  db.mutateMove('TRANSFER',{sku:record.sku,fromLocation:record.location,toLocation:target,quantity:4,reference:'TR-TEST'});
  assert.equal(initialStock(db,record.sku),before);assert.equal(db.stock(record.sku,record.location),source-4);assert.equal(db.stock(record.sku,target),destination+4);
});
test('same-location transfers and zero quantities are rejected',()=>{
  const db=fresh();
  assert.throws(()=>db.mutateMove('TRANSFER',{sku:'SKU-1001',fromLocation:'A-01',toLocation:'A-01',quantity:1,reference:'TR-BAD'}),/different locations/);
  assert.throws(()=>db.mutateMove('RECEIVE',{sku:'SKU-1001',toLocation:'A-01',quantity:0,reference:'PO-BAD'}),/greater than zero/);
});
test('warehouse bin capacity is respected',()=>{
  const db=fresh();const before=db.movements().length;
  assert.throws(()=>db.mutateMove('RECEIVE',{sku:'SKU-1001',toLocation:'A-01',quantity:2000,reference:'PO-CAPACITY'}),/capacity exceeded/);
  assert.equal(db.movements().length,before);
});
test('creating duplicate SKU is rejected and products may be deactivated',()=>{
  const db=fresh();assert.throws(()=>db.createProduct({sku:'SKU-1001',name:'Copy',category:'Other',unitPriceCents:100,reorderLevel:3}),/already exists/);
  const id=db.createProduct({sku:'SKU-9999',name:'New product',category:'Other',unitPriceCents:100,reorderLevel:3}).id;
  db.updateProduct(id,{name:'New product',category:'Other',unitPriceCents:100,reorderLevel:3,isActive:false});
  assert.equal(db.productList().find(p=>p.id===id).isActive,0);
});
test('reset restores stock and movement history',()=>{
  const db=fresh();const original=db.dashboard().totalUnits;
  db.mutateMove('RECEIVE',{sku:'SKU-1001',toLocation:'A-01',quantity:10,reference:'PO-RESET'});
  assert.equal(db.dashboard().totalUnits,original+10);
  db.reset();assert.equal(db.dashboard().totalUnits,original);
});
test('reports export correctly quoted CSV',()=>{
  const db=fresh();const result=db.csv('inventory');
  assert.match(result,/"sku","name","category","location","zone","quantity"/);
  assert.match(db.csv('movements'),/"createdUtc"/);
});
