package vfscache

import (
	"github.com/rclone/rclone/fs/rc"
	"github.com/stretchr/testify/require"
	"testing"
	"time"
)

func TestCloudSSDStatsDoesNotWaitForBusyItem(t *testing.T) {
	_, c := newTestCache(t)
	item := c.Item("busy-file")
	require.NoError(t, item.Open(nil))
	item.mu.Lock()
	done := make(chan rc.Params, 1)
	go func() { done <- c.Stats() }()
	var stats rc.Params
	select {
	case stats = <-done:
	case <-time.After(250 * time.Millisecond):
		item.mu.Unlock()
		<-done
		require.NoError(t, item.Close(nil))
		t.Fatal("drive stats waited for a busy file mutex")
	}
	item.mu.Unlock()
	require.Equal(t, 1, stats["openFiles"])
	require.NoError(t, item.Close(nil))
	require.Equal(t, 0, c.Stats()["openFiles"])
}
