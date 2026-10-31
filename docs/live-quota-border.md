# Live quota border

The Live row's avatar in the discussion sidebar carries a thin border showing how
much quota the Live discussion's provider has left, measured on its most
constrained window. The border fills clockwise from 12 o'clock: teal for what is
left, gold from 90% used, red at 100%. It is absent when the provider reports no
quota.

![Live row with the quota border around its avatar](images/live-quota-border.png)

Hovering or clicking the avatar opens a compact summary of every provider that
reports quota, with the Live's provider marked. Clicking the avatar does not
select the Live discussion; the rest of the row still does. The full detail stays
in Session info.

![Provider quota summary opened from the Live avatar](images/live-quota-summary.png)

The border reads the shared provider-usage cache that Session info also feeds. It
never forces a provider refresh: it asks on mount and when the Live discussion's
activity changes, and the summary asks once per provider when opened.
