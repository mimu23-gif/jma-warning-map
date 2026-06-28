#!/usr/bin/env python3
"""
region-index.json 分析スクリプト
北海道・沖縄の地域コード登録状況を確認
"""
import json
import sys
from pathlib import Path

def analyze_region_index():
    """region-index.json の構造と北海道・沖縄の状況を分析"""
    
    # ファイル読み込み
    index_path = Path("GAS/region-index.json")
    if not index_path.exists():
        print(f"❌ {index_path} が見つかりません")
        return
    
    print(f"📄 ファイルサイズ: {index_path.stat().st_size:,} bytes")
    
    try:
        with open(index_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception as e:
        print(f"❌ JSON読み込みエラー: {e}")
        return
    
    # 基本構造確認
    print(f"\n📊 基本構造:")
    print(f"  - version: {data.get('version', 'N/A')}")
    print(f"  - raw 件数: {len(data.get('raw', {})):,}")
    print(f"  - norm6 件数: {len(data.get('norm6', {})):,}")
    
    # 北海道・沖縄の状況確認
    hokkaido_codes = []
    okinawa_codes = []
    
    raw_data = data.get('raw', {})
    norm6_data = data.get('norm6', {})
    
    # 7桁コードから北海道・沖縄を抽出
    for code in raw_data.keys():
        if code.startswith('01'):  # 北海道
            hokkaido_codes.append(code)
        elif code.startswith('47'):  # 沖縄
            okinawa_codes.append(code)
    
    # 6桁正規化コードからも確認
    hokkaido_6_codes = []
    okinawa_6_codes = []
    for code in norm6_data.keys():
        if code.startswith('01'):
            hokkaido_6_codes.append(code)
        elif code.startswith('47'):
            okinawa_6_codes.append(code)
    
    print(f"\n🗾 北海道 (01*****):")
    print(f"  - 7桁コード: {len(hokkaido_codes)} 件")
    if hokkaido_codes:
        print(f"    先頭10件: {sorted(hokkaido_codes)[:10]}")
        # fileId の分布確認
        file_ids = set()
        for code in hokkaido_codes[:20]:  # 最初の20件
            entry = raw_data.get(code, {})
            if 'i' in entry:
                file_ids.add(entry['i'])
        print(f"    参照fileId: {len(file_ids)} 種類")
        for fid in sorted(file_ids)[:5]:
            print(f"      {fid}")
    
    print(f"  - 6桁正規化: {len(hokkaido_6_codes)} 件")
    if hokkaido_6_codes:
        print(f"    例: {sorted(hokkaido_6_codes)[:5]}")
    
    print(f"\n🏝️ 沖縄 (47*****):")
    print(f"  - 7桁コード: {len(okinawa_codes)} 件")
    if okinawa_codes:
        print(f"    先頭10件: {sorted(okinawa_codes)[:10]}")
        # fileId の分布確認
        file_ids = set()
        for code in okinawa_codes[:20]:
            entry = raw_data.get(code, {})
            if 'i' in entry:
                file_ids.add(entry['i'])
        print(f"    参照fileId: {len(file_ids)} 種類")
        for fid in sorted(file_ids)[:5]:
            print(f"      {fid}")
    
    print(f"  - 6桁正規化: {len(okinawa_6_codes)} 件")
    if okinawa_6_codes:
        print(f"    例: {sorted(okinawa_6_codes)[:5]}")
    
    # JMA警報取得で使われる具体的な6桁コードの確認
    target_6_codes = ['010000', '470000']  # 都道府県レベル
    print(f"\n🎯 都道府県コード確認:")
    for code in target_6_codes:
        if code in norm6_data:
            entry = norm6_data[code]
            print(f"  {code}: ✅ 登録済み -> fileId={entry.get('i', 'N/A')}, representative={entry.get('r', 'N/A')}")
        else:
            print(f"  {code}: ❌ 未登録")
    
    # 全体的な fileId 分布（地域特性確認）
    all_file_ids = set()
    for entry in raw_data.values():
        if 'i' in entry:
            all_file_ids.add(entry['i'])
    
    print(f"\n📁 全体 fileId 分布:")
    print(f"  - 総fileId数: {len(all_file_ids)}")
    print(f"  - 例: {sorted(all_file_ids)[:10]}")

if __name__ == "__main__":
    analyze_region_index()
