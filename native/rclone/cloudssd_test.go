package vfscache

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config"
	"github.com/rclone/rclone/fstest"
	"github.com/rclone/rclone/lib/ranges"
	"github.com/rclone/rclone/vfs/vfscommon"
	"github.com/stretchr/testify/require"
)

type cloudSSDDelayedObject struct {
	fs.Object
	started chan struct{}
	release chan struct{}
	once    sync.Once
}

func (o *cloudSSDDelayedObject) Open(ctx context.Context, opts ...fs.OpenOption) (io.ReadCloser, error) {
	o.once.Do(func() { close(o.started) })
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-o.release:
	}
	return o.Object.Open(ctx, opts...)
}

func TestCloudSSDCloseDefersHydrationAndReopenCancelsIt(t *testing.T) {
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(50 * time.Millisecond)
	r, c := newTestCacheOpt(t, opt)
	original, obj, item := newFileLength(t, r, c, "game.pak", 2*1024*1024+19)
	delayed := &cloudSSDDelayedObject{Object: obj, started: make(chan struct{}), release: make(chan struct{})}
	require.NoError(t, item.Open(delayed))
	n, err := item.WriteAt([]byte("PATCH"), 17)
	require.NoError(t, err)
	require.Equal(t, 5, n)
	start := time.Now()
	closed := make(chan error, 1)
	go func() { closed <- item.Close(nil) }()
	select {
	case err = <-closed:
		require.NoError(t, err)
	case <-time.After(2 * time.Second):
		close(delayed.release)
		<-closed
		t.Fatal("filesystem close waited for cloud data")
	}
	require.Less(t, time.Since(start), 2*time.Second, "close waited for cloud data")
	item.mu.Lock()
	dirty, present := item.info.Dirty, item.info.Rs.Size()
	item.mu.Unlock()
	require.True(t, dirty)
	require.Equal(t, int64(5), present, "unknown sparse bytes must not be marked present")
	select {
	case <-delayed.started:
	case <-time.After(5 * time.Second):
		t.Fatal("background hydration did not start")
	}
	reopened := make(chan error, 1)
	go func() { reopened <- item.Open(delayed) }()
	select {
	case err = <-reopened:
		require.NoError(t, err)
	case <-time.After(3 * time.Second):
		t.Fatal("reopen did not cancel background hydration")
	}
	b := make([]byte, 5)
	_, err = item.ReadAt(b, 17)
	require.NoError(t, err)
	require.Equal(t, "PATCH", string(b))
	n, err = item.WriteAt([]byte("TAIL"), int64(len(original)-4))
	require.NoError(t, err)
	require.Equal(t, 4, n)
	close(delayed.release)
	require.NoError(t, item.Close(nil))
	require.Eventually(t, func() bool { item.mu.Lock(); defer item.mu.Unlock(); return !item.info.Dirty }, 10*time.Second, 20*time.Millisecond)
	want := []byte(original)
	copy(want[17:], "PATCH")
	copy(want[len(want)-4:], "TAIL")
	checkObject(t, r, "game.pak", string(want))
	item.mu.Lock()
	complete := item.info.Rs.Present(ranges.Range{Pos: 0, Size: int64(len(want))})
	item.mu.Unlock()
	require.True(t, complete)
}

// Recreate the cache from its persisted sparse metadata, with the old worker gone.
type cloudSSDDelayedFS struct {
	fs.Fs
	started, release chan struct{}
}

func (f cloudSSDDelayedFS) NewObject(ctx context.Context, name string) (fs.Object, error) {
	o, err := f.Fs.NewObject(ctx, name)
	if err != nil {
		return nil, err
	}
	return &cloudSSDDelayedObject{Object: o, started: f.started, release: f.release, once: sync.Once{}}, nil
}
func TestCloudSSDRestartQueuesSparseWritesWithoutWaitingForCloud(t *testing.T) {
	r := fstest.NewRun(t)
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(time.Hour)
	ctx1, cancel1 := context.WithCancel(context.Background())
	defer cancel1()
	c1, err := New(ctx1, r.Fremote, &opt, nil)
	require.NoError(t, err)
	original, obj, item := newFileLength(t, r, c1, "resume.pak", 1024*1024+29)
	require.NoError(t, item.Open(obj))
	_, err = item.WriteAt([]byte("SAVE"), 19)
	require.NoError(t, err)
	require.NoError(t, item.Close(nil))
	cancel1()
	started, release := make(chan struct{}), make(chan struct{})
	f := cloudSSDDelayedFS{Fs: r.Fremote, started: started, release: release}
	ctx2, cancel2 := context.WithCancel(context.Background())
	defer cancel2()
	opt.WriteBack = fs.Duration(50 * time.Millisecond)
	start := time.Now()
	c2, err := New(ctx2, f, &opt, func(string, int64, bool) error { return nil })
	require.NoError(t, err)
	defer func() { cancel2(); require.NoError(t, c2.CleanUp()) }()
	require.Less(t, time.Since(start), 2*time.Second, "startup waited for cloud hydration")
	restored, _ := c2.get("resume.pak")
	restored.mu.Lock()
	dirty, present := restored.info.Dirty, restored.info.Rs.Size()
	restored.mu.Unlock()
	require.True(t, dirty)
	require.Equal(t, int64(4), present)
	select {
	case <-started:
	case <-time.After(5 * time.Second):
		t.Fatal("recovered writeback never started")
	}
	close(release)
	require.Eventually(t, func() bool { restored.mu.Lock(); defer restored.mu.Unlock(); return !restored.info.Dirty }, 10*time.Second, 20*time.Millisecond)
	want := []byte(original)
	copy(want[19:], "SAVE")
	checkObject(t, r, "resume.pak", string(want))
}

// Cold recovery must leave workers available for newly completed downloads.
func TestCloudSSDColdRecoveryDoesNotStarveReadyUploads(t *testing.T) {
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(50 * time.Millisecond)
	r, c := newTestCacheOpt(t, opt)
	release := make(chan struct{})
	var releaseOnce sync.Once
	t.Cleanup(func() { releaseOnce.Do(func() { close(release) }) })
	coldItems := make([]*Item, max(2, fs.GetConfig(c.ctx).Transfers)+1)
	started := make([]chan struct{}, len(coldItems))
	originals := make([]string, len(coldItems))
	for i := range coldItems {
		name := fmt.Sprintf("cold-%d.pak", i)
		original, obj, item := newFileLength(t, r, c, name, 1024*1024+29)
		originals[i] = original
		started[i] = make(chan struct{})
		delayed := &cloudSSDDelayedObject{Object: obj, started: started[i], release: release}
		require.NoError(t, item.Open(delayed))
		_, err := item.WriteAt([]byte("PATCH"), 17)
		require.NoError(t, err)
		require.NoError(t, item.Close(nil))
		coldItems[i] = item
	}
	require.Eventually(t, func() bool {
		count := 0
		for _, ch := range started {
			select {
			case <-ch:
				count++
			default:
			}
		}
		return count >= cap(c.hydrationSlots)
	}, 5*time.Second, 10*time.Millisecond)
	ready := c.Item("ready.pak")
	require.NoError(t, ready.Open(nil))
	_, err := ready.WriteAt([]byte("new Steam download bytes"), 0)
	require.NoError(t, err)
	require.NoError(t, ready.Close(nil))
	require.Eventually(t, func() bool { ready.mu.Lock(); defer ready.mu.Unlock(); return !ready.info.Dirty }, 5*time.Second, 20*time.Millisecond, "cold recovery starved a complete file")
	checkObject(t, r, "ready.pak", "new Steam download bytes")
	releaseOnce.Do(func() { close(release) })
	for i, item := range coldItems {
		require.Eventually(t, func() bool { item.mu.Lock(); defer item.mu.Unlock(); return !item.info.Dirty }, 10*time.Second, 20*time.Millisecond)
		expected := []byte(originals[i])
		copy(expected[17:], "PATCH")
		checkObject(t, r, fmt.Sprintf("cold-%d.pak", i), string(expected))
	}
}

func TestCloudSSDMetadataFailurePreservesLastValidRanges(t *testing.T) {
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(time.Hour)
	_, c := newTestCacheOpt(t, opt)
	item := c.Item("metadata.pak")
	require.NoError(t, item.Open(nil))
	_, err := item.WriteAt([]byte("valid cached bytes"), 0)
	require.NoError(t, err)
	require.NoError(t, item.Sync())
	name := c.toOSPathMeta(item.name)
	before, err := os.ReadFile(name)
	require.NoError(t, err)
	item.mu.Lock()
	validTime := item.info.ModTime
	item.info.ModTime = time.Date(10000, 1, 1, 0, 0, 0, 0, time.UTC)
	err = item._save()
	item.info.ModTime = validTime
	item.mu.Unlock()
	require.Error(t, err)
	after, err := os.ReadFile(name)
	require.NoError(t, err)
	require.Equal(t, before, after)
	require.NoError(t, item.Close(nil))
}

func TestCloudSSDSteamProgressKeepsVerifiedVersionsOutsideTheActiveCache(t *testing.T) {
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(time.Hour)
	_, c := newTestCacheOpt(t, opt)
	name := "SteamLibrary/steamapps/appmanifest_123.acf"
	item := c.Item(name)
	identity := sha256.Sum256([]byte(name))
	journal := filepath.Join(config.GetCacheDir(), "steam-progress-journal", fmt.Sprintf("%x", identity))
	t.Cleanup(func() { _ = os.RemoveAll(journal) })
	for i := 0; i < 6; i++ {
		require.NoError(t, item.Open(nil))
		data := []byte(fmt.Sprintf("checkpoint-%d", i))
		_, err := item.WriteAt(data, 0)
		require.NoError(t, err)
		require.NoError(t, item.Close(nil))
	}
	copies, err := filepath.Glob(filepath.Join(journal, "*.json"))
	require.NoError(t, err)
	require.Len(t, copies, 4)
	foundLatest := false
	for _, copy := range copies {
		b, err := os.ReadFile(copy)
		require.NoError(t, err)
		var record struct {
			Path, SHA256 string
			Data         []byte
			Metadata     json.RawMessage
		}
		require.NoError(t, json.Unmarshal(b, &record))
		require.Equal(t, name, record.Path)
		require.Equal(t, fmt.Sprintf("%x", sha256.Sum256(record.Data)), record.SHA256)
		if string(record.Data) == "checkpoint-5" {
			foundLatest = true
		}
	}
	require.True(t, foundLatest)
	require.True(t, cloudSSDProgressPath("SteamLibrary/steamapps/downloading/state_123_456.patch"))
	require.False(t, cloudSSDProgressPath("SteamLibrary/steamapps/common/game.pak"))
}
