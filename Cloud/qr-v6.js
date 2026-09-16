'use strict';

// Minimal dependency-free QR encoder used only for short TOR POS pairing URLs.
// Fixed profile: QR Model 2, Version 6, error correction L, byte mode, mask 0.
// Version 6-L carries up to 134 UTF-8 bytes in byte mode.
const VERSION=6;
const SIZE=41;
const DATA_CODEWORDS=136;
const BLOCKS=2;
const DATA_PER_BLOCK=68;
const ECC_PER_BLOCK=18;
const TOTAL_CODEWORDS=172;

function appendBits(out,value,length){
  for(let i=length-1;i>=0;i--)out.push((value>>>i)&1);
}
function gfMultiply(x,y){
  let z=0;
  for(let i=7;i>=0;i--){
    z=((z<<1)^(((z>>>7)&1)*0x11D))&0xFF;
    z^=((y>>>i)&1)*x;
  }
  return z;
}
function rsDivisor(degree){
  const result=new Uint8Array(degree);result[degree-1]=1;let root=1;
  for(let i=0;i<degree;i++){
    for(let j=0;j<result.length;j++){
      result[j]=gfMultiply(result[j],root);
      if(j+1<result.length)result[j]^=result[j+1];
    }
    root=gfMultiply(root,0x02);
  }
  return result;
}
const RS18=rsDivisor(ECC_PER_BLOCK);
function rsRemainder(data){
  const result=new Uint8Array(ECC_PER_BLOCK);
  for(const b of data){
    const factor=b^result[0];
    result.copyWithin(0,1);result[result.length-1]=0;
    for(let i=0;i<result.length;i++)result[i]^=gfMultiply(RS18[i],factor);
  }
  return result;
}
function formatBits(mask){
  const data=(1<<3)|mask; // L = format bits 01
  let rem=data<<10;
  for(let i=14;i>=10;i--)if(((rem>>>i)&1)!==0)rem^=0x537<<(i-10);
  return ((data<<10)|rem)^0x5412;
}
function makeQrV6L(text){
  const bytes=Buffer.from(String(text),'utf8');
  if(bytes.length>134)throw new Error('QR pairing URL exceeds Version 6-L byte capacity.');
  const bits=[];
  appendBits(bits,0x4,4); // byte mode
  appendBits(bits,bytes.length,8);
  for(const b of bytes)appendBits(bits,b,8);
  const capacity=DATA_CODEWORDS*8;
  for(let i=0;i<Math.min(4,capacity-bits.length);i++)bits.push(0);
  while(bits.length%8)bits.push(0);
  const data=[];
  for(let i=0;i<bits.length;i+=8){let v=0;for(let j=0;j<8;j++)v=(v<<1)|bits[i+j];data.push(v);}
  for(let pad=0;data.length<DATA_CODEWORDS;pad++)data.push((pad&1)===0?0xEC:0x11);

  const blocks=[];const ecc=[];
  for(let b=0;b<BLOCKS;b++){
    const part=Uint8Array.from(data.slice(b*DATA_PER_BLOCK,(b+1)*DATA_PER_BLOCK));
    blocks.push(part);ecc.push(rsRemainder(part));
  }
  const codewords=[];
  for(let i=0;i<DATA_PER_BLOCK;i++)for(let b=0;b<BLOCKS;b++)codewords.push(blocks[b][i]);
  for(let i=0;i<ECC_PER_BLOCK;i++)for(let b=0;b<BLOCKS;b++)codewords.push(ecc[b][i]);
  if(codewords.length!==TOTAL_CODEWORDS)throw new Error('QR codeword interleave failure.');

  const modules=Array.from({length:SIZE},()=>Array(SIZE).fill(false));
  const func=Array.from({length:SIZE},()=>Array(SIZE).fill(false));
  const setFunction=(x,y,dark)=>{if(x>=0&&y>=0&&x<SIZE&&y<SIZE){modules[y][x]=!!dark;func[y][x]=true;}};
  const drawFinder=(cx,cy)=>{
    for(let dy=-4;dy<=4;dy++)for(let dx=-4;dx<=4;dx++){
      const dist=Math.max(Math.abs(dx),Math.abs(dy));
      setFunction(cx+dx,cy+dy,dist!==2&&dist!==4);
    }
  };
  const drawAlignment=(cx,cy)=>{
    for(let dy=-2;dy<=2;dy++)for(let dx=-2;dx<=2;dx++)setFunction(cx+dx,cy+dy,Math.max(Math.abs(dx),Math.abs(dy))!==1);
  };

  // Timing first; finder patterns overwrite their corner intersections.
  for(let i=0;i<SIZE;i++){
    setFunction(6,i,(i&1)===0);
    setFunction(i,6,(i&1)===0);
  }
  drawFinder(3,3);drawFinder(SIZE-4,3);drawFinder(3,SIZE-4);
  drawAlignment(34,34);

  const fmt=formatBits(0);
  const getFmt=i=>((fmt>>>i)&1)!==0;
  for(let i=0;i<=5;i++)setFunction(8,i,getFmt(i));
  setFunction(8,7,getFmt(6));
  setFunction(8,8,getFmt(7));
  setFunction(7,8,getFmt(8));
  for(let i=9;i<15;i++)setFunction(14-i,8,getFmt(i));
  for(let i=0;i<8;i++)setFunction(SIZE-1-i,8,getFmt(i));
  for(let i=8;i<15;i++)setFunction(8,SIZE-15+i,getFmt(i));
  setFunction(8,SIZE-8,true); // fixed dark module

  let bitIndex=0;
  const getCodeBit=index=>((codewords[index>>>3]>>>(7-(index&7)))&1)!==0;
  for(let right=SIZE-1;right>=1;right-=2){
    if(right===6)right=5;
    for(let vert=0;vert<SIZE;vert++){
      const y=(((right+1)&2)===0)?SIZE-1-vert:vert;
      for(let j=0;j<2;j++){
        const x=right-j;
        if(func[y][x])continue;
        let dark=false;
        if(bitIndex<codewords.length*8)dark=getCodeBit(bitIndex++);
        // Fixed mask pattern 0: (row + column) mod 2 == 0.
        if(((x+y)&1)===0)dark=!dark;
        modules[y][x]=dark;
      }
    }
  }
  return modules.map(row=>row.map(x=>x?'1':'0').join(''));
}
// R122 (İ6): qrSvg was removed. It rendered a matrix as SVG for a server-side
// use that never arrived - the pairing endpoint returns qr_matrix and the
// browser draws it client-side. The encoder above is the part that is actually
// used and is unchanged.
module.exports={makeQrV6L,VERSION,SIZE};
