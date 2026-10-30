"""Generates the three fictitious HTML inputs of the demo: header.html, footer.html and
content.html (a price quotation with enough line items to span several pages).

The company, the customer and every amount are invented.

Usage: python generate_samples.py [output-folder]
"""

import sys
from pathlib import Path

STYLE = """
        body { font-family: sans-serif; font-size: 12px; color: #111; margin: 0; padding: 0; line-height: 15px; }
        table { font-size: 12px; border-collapse: collapse; }
        .muted { color: #555; }
"""

HEADER = f"""<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>Quotation header</title>
    <style>{STYLE}
        .logo {{ width: 120px; height: 120px; background: #2f4f6f; color: #fff; font-size: 40px; font-weight: bold;
                 line-height: 120px; text-align: center; border-radius: 8px; }}
        .company {{ font-size: 11px; margin-top: 8px; }}
        .info {{ width: 100%; border: 1px solid #bbb; font-size: 11px; }}
        .info h2 {{ margin: 6px; text-align: center; font-size: 20px; }}
        .info td {{ padding: 2px 6px; vertical-align: top; }}
        .label {{ width: 17%; color: #444; white-space: nowrap; }}
    </style>
</head>
<body>
<table style="width: 100%;">
    <tr>
        <td style="width: 30%; vertical-align: top;">
            <div class="logo">EO</div>
            <div class="company"><b>Example Office Supplies Ltd.</b><br>100 Sample Street, Exampleton<br>sales@example.com</div>
        </td>
        <td style="vertical-align: top;">
            <table class="info">
                <tr><td colspan="4" style="border-bottom: 1px solid #ddd;"><h2>Quotation</h2></td></tr>
                <tr>
                    <td class="label">Quotation number</td><td>Q-2026-00042</td>
                    <td class="label">Date</td><td>2026-03-19</td>
                </tr>
                <tr>
                    <td class="label">Customer</td><td>Sample Trading Co.</td>
                    <td class="label">Valid until</td><td>2026-04-18</td>
                </tr>
                <tr>
                    <td class="label">Address</td><td>200 Example Avenue, Sampletown</td>
                    <td class="label">Prepared by</td><td>Sales desk</td>
                </tr>
                <tr>
                    <td class="label">Customer tax ID</td><td>TAX-000-0000</td>
                    <td class="label">Payment terms</td><td>30 days net</td>
                </tr>
                <tr>
                    <td class="label">Project</td><td>Office refurbishment</td>
                    <td class="label">Currency</td><td>USD</td>
                </tr>
            </table>
        </td>
    </tr>
</table>
</body>
</html>
"""

FOOTER = f"""<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>Quotation footer</title>
    <style>{STYLE}
        .sign td {{ padding-bottom: 24px; font-size: 12px; }}
        .legal {{ font-size: 10px; }}
    </style>
</head>
<body>
<table style="width: 100%;" class="sign">
    <tr>
        <td align="left"><b>Date</b>: ____________________</td>
        <td align="center"><b>Customer name</b>: ________________________</td>
        <td align="right"><b>Signature</b>: ________________________</td>
    </tr>
</table>
<div class="legal"><b>This quotation was generated automatically. Prices are fictitious sample data.</b></div>
<hr style="border: 0; border-top: 1px solid #111; margin: 6px 0;">
<table style="width: 100%;" class="legal">
    <tr>
        <td align="left">Example Office Supplies Ltd., 100 Sample Street, Exampleton. Tel: +1 555 0100</td>
        <td align="right"><b>Company no.</b> 00000000 &nbsp; <b>Tax ID</b> TAX-111-1111</td>
    </tr>
</table>
</body>
</html>
"""

PRODUCTS = [
    ("Furniture", "Height-adjustable desk, 160 x 80 cm", 349.00),
    ("Furniture", "Ergonomic office chair, mesh back", 219.00),
    ("Furniture", "Mobile pedestal, 3 drawers", 129.00),
    ("Lighting", "LED desk lamp with dimmer", 39.90),
    ("Electronics", "27-inch monitor, height-adjustable stand", 189.00),
    ("Electronics", "Docking station, USB-C", 149.00),
    ("Supplies", "Whiteboard, 120 x 90 cm, magnetic", 84.50),
    ("Supplies", "Recycled paper, A4, box of 5 reams", 24.75),
    ("Storage", "Filing cabinet, 4 drawers, lockable", 259.00),
    ("Acoustics", "Acoustic desk screen, 140 cm", 112.00),
]


def content():
    rows = []
    subtotal = 0.0
    for i in range(48):
        category, description, price = PRODUCTS[i % len(PRODUCTS)]
        qty = 2 + (i * 7) % 11
        amount = qty * price
        subtotal += amount
        rows.append(
            f'            <tr><td>{i + 1:03d}</td><td>{category}</td><td>{description}</td>'
            f'<td class="num">{qty}</td><td class="num">{price:,.2f}</td><td class="num">{amount:,.2f}</td></tr>')
    tax = round(subtotal * 0.08, 2)
    rows.append(f'            <tr class="total"><td colspan="5">Subtotal</td><td class="num">{subtotal:,.2f}</td></tr>')
    rows.append(f'            <tr class="total"><td colspan="5">Sales tax (8 %)</td><td class="num">{tax:,.2f}</td></tr>')
    rows.append(f'            <tr class="total"><td colspan="5">Total</td><td class="num">{subtotal + tax:,.2f}</td></tr>')
    body = "\n".join(rows)
    return f"""<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>Quotation content</title>
    <style>{STYLE}
        .items {{ width: 100%; font-size: 11px; border: 1px solid #999; }}
        .items th {{ background: #e8edf2; text-align: left; }}
        .items th, .items td {{ padding: 5px; border-bottom: 1px solid #ccc; border-right: 1px solid #ccc; }}
        .items .num {{ text-align: right; }}
        .items .total td {{ font-weight: bold; text-align: right; }}
    </style>
</head>
<body>
<table class="items">
    <tr>
        <th style="width: 6%;">#</th><th style="width: 14%;">Category</th><th style="width: 44%;">Description</th>
        <th style="width: 8%;" class="num">Qty</th><th style="width: 13%;" class="num">Unit price</th><th style="width: 15%;" class="num">Amount</th>
    </tr>
{body}
</table>
</body>
</html>
"""


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent
    (out / "header.html").write_text(HEADER, encoding="utf-8")
    (out / "footer.html").write_text(FOOTER, encoding="utf-8")
    (out / "content.html").write_text(content(), encoding="utf-8")
    print(f"Wrote header.html, footer.html and content.html to {out}")


if __name__ == "__main__":
    main()
