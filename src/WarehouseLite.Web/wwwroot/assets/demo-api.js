// Browser storage adapter for running the UI without .NET.
const STORE_KEY = 'hako-demo-v4';
const clone = value => JSON.parse(JSON.stringify(value));
const int = value => Number.parseInt(value, 10) || 0;
const nowUtc = () => new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');

export class DemoWarehouse {
  constructor(seed, options = {}) {
    this.persist = options.persist !== false;
    this.storage = options.storage;
    if (this.storage === undefined) { try { this.storage = localStorage; } catch { this.storage = null; } }
    this.seed = seed;
    const saved = this.persist && this.storage ? this.storage.getItem(STORE_KEY) : null;
    try { this.state = saved ? JSON.parse(saved) : this.buildSeed(seed); }
    catch { this.state = this.buildSeed(seed); }
  }
  buildSeed(seed) {
    const state = {
      products: seed.products.map((p, i) => ({ ...p, id: i + 1, isActive: 1 })),
      locations: seed.locations.map((l, i) => ({ ...l, id: i + 1 })),
      inventory: {}, movements: [], nextId: 1
    };
    for (const m of seed.movements) this.move(state, {
      sku: m.sku, quantity: m.quantity, type: m.type,
      fromLocation: m.fromLocation || null, toLocation: m.to || null,
      reference: m.reference, note: m.note, createdUtc: m.timestamp
    });
    return state;
  }
  reset() { this.state = this.buildSeed(this.seed); this.save(); return { success: true }; }
  save() { if (this.persist && this.storage) this.storage.setItem(STORE_KEY, JSON.stringify(this.state)); }
  key(sku, location) { return `${sku}|${location}`; }
  stock(sku, loc) { return this.state.inventory[this.key(sku, loc)] || 0; }
  available(sku, loc) { return loc ? this.stock(sku, loc) : Object.entries(this.state.inventory).filter(([k]) => k.startsWith(sku + '|')).reduce((a, [,n]) => a+n,0); }
  productList(query='', category='', filter='all') {
    const q=query.toLowerCase();
    return this.state.products.map(p => {
      const stock=this.available(p.sku);
      const locationCount=Object.keys(this.state.inventory).filter(k => k.startsWith(p.sku+'|') && this.state.inventory[k]>0).length;
      return {...p,stock,locationCount};
    }).filter(p => (!q || p.sku.toLowerCase().includes(q) || p.name.toLowerCase().includes(q))
      && (!category || p.category === category)
      && (filter==='all' || (filter==='low' && p.stock<=p.reorderLevel && p.isActive===1)
      || (filter==='in-stock' && p.stock>0 && p.isActive===1) || (filter==='inactive' && p.isActive===0)))
      .sort((a,b)=>b.isActive-a.isActive || a.name.localeCompare(b.name));
  }
  locationList() {
    return this.state.locations.map(l=>({ ...l,stock:Object.entries(this.state.inventory).filter(([key])=>key.endsWith('|'+l.code)).reduce((a,[,qty])=>a+qty,0),skuCount:Object.entries(this.state.inventory).filter(([key,qty])=>key.endsWith('|'+l.code)&&qty>0).length}));
  }
  inventoryList(query='') {
    const q=query.toLowerCase();const out=[];
    for(const [key,quantity] of Object.entries(this.state.inventory)){
      if(quantity<=0)continue;
      const [sku,location]=key.split('|'); const p=this.state.products.find(p=>p.sku===sku);const l=this.state.locations.find(l=>l.code===location);
      if(p&&l&&(!q||sku.toLowerCase().includes(q)||p.name.toLowerCase().includes(q)||location.toLowerCase().includes(q)))out.push({sku,name:p.name,category:p.category,location,zone:l.zone,quantity});
    }
    return out.sort((a,b)=>a.name.localeCompare(b.name)||a.location.localeCompare(b.location));
  }
  movements(limit=120) { return clone(this.state.movements).sort((a,b)=>b.createdUtc.localeCompare(a.createdUtc)||b.id-a.id).slice(0,Math.max(1,Number(limit))); }
  dashboard() {
    const ps=this.productList();const ls=this.locationList();const ms=this.movements(500);
    const low=ps.filter(p=>p.stock<=p.reorderLevel&&p.isActive===1);
    return {totalSkus:ps.filter(p=>p.isActive===1).length,totalUnits:ls.reduce((a,l)=>a+l.stock,0),lowStock:low.length,
      inventoryValueCents:ps.reduce((a,p)=>a+p.stock*p.unitPriceCents,0),locationCount:ls.length,recentMovements:ms.slice(0,7),
      lowStockProducts:low.sort((a,b)=>a.stock-b.stock).slice(0,6),movementHistory:ms};
  }
  move(state, move) {
    const {sku,quantity,type,fromLocation,toLocation,reference,note='',createdUtc}=move;
    if(!Number.isSafeInteger(quantity)||quantity<=0)throw new Error('Quantity must be greater than zero.');
    if(!reference?.trim())throw new Error('Reference is required.');
    const p=state.products.find(p=>p.sku.toLowerCase()===String(sku).toLowerCase()&&p.isActive===1);
    if(!p)throw new Error('Product not found or inactive.');
    const validLocation=loc=>state.locations.some(l=>l.code===loc);
    if(type==='RECEIVE'&&!validLocation(toLocation))throw new Error('Location not found.');
    if(type==='DISPATCH'&&!validLocation(fromLocation))throw new Error('Location not found.');
    if(type==='TRANSFER'&&(!validLocation(fromLocation)||!validLocation(toLocation)))throw new Error('Location not found.');
    if(type==='TRANSFER'&&fromLocation===toLocation)throw new Error('Choose two different locations.');
    const fk=this.key(p.sku,fromLocation),tk=this.key(p.sku,toLocation);
    if(type!=='DISPATCH') {
      const dest=state.locations.find(l=>l.code===toLocation);
      const occupied=Object.entries(state.inventory).filter(([key])=>key.endsWith('|'+toLocation)).reduce((a,[,qty])=>a+qty,0);
      if(occupied+quantity>dest.capacityUnits)throw new Error('Location capacity exceeded.');
    }
    // Verify before any modification: transfer is atomic.
    if(type!=='RECEIVE' && (state.inventory[fk]||0)<quantity)throw new Error('Insufficient stock at the selected location.');
    if(type==='RECEIVE')state.inventory[tk]=(state.inventory[tk]||0)+quantity;
    else if(type==='DISPATCH')state.inventory[fk]-=quantity;
    else if(type==='TRANSFER'){state.inventory[fk]-=quantity;state.inventory[tk]=(state.inventory[tk]||0)+quantity;}
    else throw new Error('Unknown movement type.');
    const id=state.nextId++;
    state.movements.push({id,type,sku:p.sku,name:p.name,quantity,fromLocation:fromLocation||null,toLocation:toLocation||null,reference:reference.trim(),note,createdUtc:createdUtc||nowUtc()});
    return {id};
  }
  mutateMove(type, body) { const result=this.move(this.state,{...body,type});this.save();return result; }
  createProduct(body) {
    if(!body.sku?.trim()||!body.name?.trim()||!body.category?.trim())throw new Error('SKU, name, and category are required.');
    if(body.unitPriceCents<0||body.reorderLevel<0)throw new Error('Price and reorder level cannot be negative.');
    if(this.state.products.some(p=>p.sku.toLowerCase()===body.sku.toLowerCase()))throw new Error('That SKU already exists.');
    const id=Math.max(0,...this.state.products.map(p=>p.id))+1;
    this.state.products.push({...body,sku:body.sku.trim(),id,isActive:1});this.save();return {id};
  }
  updateProduct(id, body) {
    const p=this.state.products.find(p=>p.id===id);if(!p)throw new Error('Product not found.');
    if(!body.name?.trim()||!body.category?.trim()||body.unitPriceCents<0||body.reorderLevel<0)throw new Error('Enter valid product details.');
    Object.assign(p,body,{isActive:body.isActive?1:0});this.save();return {success:true};
  }
  async request(path,options={}) {
    const url=new URL(path,'http://localhost');const method=options.method||'GET';const body=options.body||{};
    if(url.pathname==='/api/dashboard')return this.dashboard();
    if(url.pathname==='/api/products'&&method==='GET')return this.productList(url.searchParams.get('query')||'',url.searchParams.get('category')||'',url.searchParams.get('filter')||'all');
    if(url.pathname==='/api/products'&&method==='POST')return this.createProduct(body);
    if(url.pathname.startsWith('/api/products/')&&method==='PUT')return this.updateProduct(int(url.pathname.split('/').pop()),body);
    if(url.pathname==='/api/locations')return this.locationList();
    if(url.pathname==='/api/inventory')return this.inventoryList(url.searchParams.get('query')||'');
    if(url.pathname==='/api/movements')return this.movements(int(url.searchParams.get('limit')||'150'));
    if(url.pathname==='/api/movements/receive')return this.mutateMove('RECEIVE',body);
    if(url.pathname==='/api/movements/dispatch')return this.mutateMove('DISPATCH',body);
    if(url.pathname==='/api/movements/transfer')return this.mutateMove('TRANSFER',body);
    if(url.pathname==='/api/demo/reset'&&method==='POST')return this.reset();
    throw new Error(`No demo endpoint: ${method} ${url.pathname}`);
  }
  csv(type) {
    const rows=type==='inventory'?this.inventoryList():this.movements(1000);
    if(!rows.length)return 'No records\r\n';
    const keys=Object.keys(rows[0]);const escape=v=>'"'+String(v??'').replaceAll('"','""')+'"';
    return '\uFEFF'+[keys.map(escape).join(','),...rows.map(row=>keys.map(key=>escape(row[key])).join(','))].join('\r\n');
  }
}
