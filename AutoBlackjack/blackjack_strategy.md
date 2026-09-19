Strategy:

Bet sizing (Hi-Lo count-based):
    True Count ≤ 1: minimum bet
    True Count  2:  2× minimum
    True Count  3:  4× minimum
    True Count  4:  6× minimum
    True Count ≥ 5: maximum bet

Card Counting (Hi-Lo):
    Cards 2-6: +1
    Cards 7-9:  0
    Cards 10, J, Q, K, A: -1
    True Count = Running Count / Estimated Decks Remaining
    Assumes 6-deck shoe. Running count persists across rounds.
    All visible cards (all players + dealer) are scanned at decision time.
    Dealer reveal is also counted (Hook 4) for next-round accuracy.

Illustrious 18 Deviations (applied when True Count thresholds met):
    16 vs 10: STAND when TC ≥ 0 (basic says HIT)
    16 vs  9: STAND when TC ≥ 5 (basic says HIT)
    15 vs 10: STAND when TC ≥ 4 (basic says HIT)
    12 vs  3: STAND when TC ≥ 2 (basic says HIT)
    12 vs  2: STAND when TC ≥ 3 (basic says HIT)
    10 vs 10: DOUBLE when TC ≥ 4 (basic says HIT)
    10 vs  A: DOUBLE when TC ≥ 4 (basic says HIT)
     9 vs  2: DOUBLE when TC ≥ 1 (basic says HIT)
     9 vs  7: DOUBLE when TC ≥ 3 (basic says HIT)

Soft totals: A soft total is any hand that has an Ace as one of the first two cards, the ace counts as 11 to start.

    Soft 20 (A,9) always stands.
    Soft 19 (A,8) doubles against dealer 6, otherwise stand.
    Soft 18 (A,7) doubles against dealer 2 through 6, and hits against 9 through Ace, otherwise stand.
    Soft 17 (A,6) doubles against dealer 3 through 6, otherwise hit.
    Soft 16 (A,5) doubles against dealer 4 through 6, otherwise hit.
    Soft 15 (A,4) doubles against dealer 4 through 6, otherwise hit.
    Soft 14 (A,3) doubles against dealer 5 through 6, otherwise hit.
    Soft 13 (A,2) doubles against dealer 5 through 6, otherwise hit.

Hard totals: A hard total is any hand that does not start with an ace in it, or it has been dealt an ace that can only be counted as 1 instead of 11.

    17 and up always stands.
    16 stands against dealer 2 through 6, otherwise hit.
    15 stands against dealer 2 through 6, otherwise hit.
    14 stands against dealer 2 through 6, otherwise hit.
    13 stands against dealer 2 through 6, otherwise hit.
    12 stands against dealer 4 through 6, otherwise hit.
    11 always doubles.
    10 doubles against dealer 2 through 9 otherwise hit.
    9 doubles against dealer 3 through 6 otherwise hit.
    8 always hits.

Splits:
    Aces    : always split
    10      : always stand        
    9       : split vs dealer 2-9 except 7, else stand
    8       : always split
    7       : split vs dealer 2-7, else hit
    6       : split vs dealer 2-6, else hit
    5       : double vs dealer 2-9, else hit
    4       : split vs dealer 5-6, else hit
    1-3       : split vs dealer 2-7, else hit