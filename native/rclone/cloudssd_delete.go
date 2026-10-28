package vfscache

import (
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config"
	"github.com/rclone/rclone/lib/diskusage"
	"github.com/rclone/rclone/lib/file"
	"github.com/rclone/rclone/lib/ranges"
)

func cloudSSDSteamDataPath(name string) bool {
	parts := strings.Split(strings.ToLower(strings.ReplaceAll(name, `\`, "/")), "/")
	for i := 0; i+3 < len(parts); i++ {
		if parts[i] == "steamapps" && parts[i+1] == "downloading" {
			if id, err := strconv.ParseUint(parts[i+2], 10, 32); err == nil && id > 0 && parts[i+3] != "" {
				return true
			}
		}
	}
	return false
}

type cloudSSDDeletedRange struct {
	ranges.Range
	SHA256 string
}

type cloudSSDDeletedData struct {
	Version     int
	Path        string
	Remote      string
	SavedAt     time.Time
	Warning     string
	Info        Info
	ValidRanges []cloudSSDDeletedRange
}

// PreserveProtectedDelete saves locally modified Steam staging data BEFORE the
// upstream remove path cancels uploads and unlinks the cache. Remote deletion
// protection alone preserves only the old uploaded version. Sparse holes remain
// explicitly missing; these snapshots are never automatically restored over Steam.
//
// The caller holds File.muRW to exclude new external opens until deletion ends.
// Existing handles/accesses must be closed. We hold Item.mu while copying known
// ranges so internal hydration cannot change the range map mid-snapshot. No
// network request, downloader cancellation or writeback lock runs under Item.mu.
func (c *Cache) PreserveProtectedDelete(name string) error {
	name = clean(name)
	if !cloudSSDSteamDataPath(name) {
		return nil
	}
	c.mu.Lock()
	item := c.item[name]
	c.mu.Unlock()
	if item == nil {
		return nil
	}
	// A filesystem delete must not wait on a file mutex held during remote I/O.
	if !item.mu.TryLock() {
		return fmt.Errorf("Steam staging data is busy; retry deletion: %w", syscall.EBUSY)
	}
	defer item.mu.Unlock()
	if item.opens != 0 || item.pendingAccesses != 0 || item.beingReset || item.closing != nil {
		return fmt.Errorf("Steam staging data is in use; retry deletion after it closes: %w", syscall.EBUSY)
	}
	if !item.info.Dirty {
		return nil
	}

	info := item.info
	info.Rs = append(ranges.Ranges(nil), info.Rs...)
	if info.Size < 0 {
		return fmt.Errorf("invalid Steam staging size; refusing deletion")
	}
	var required int64 = 1024 * 1024
	for _, r := range info.Rs {
		if r.Pos < 0 || r.Size <= 0 || r.Pos > info.Size || r.Size > info.Size-r.Pos {
			return fmt.Errorf("invalid Steam staging range; refusing deletion")
		}
		// Allow for per-range allocation rounding and the recovery metadata.
		required += r.Size + 65536
		if required < 0 {
			return fmt.Errorf("Steam staging recovery size overflow; refusing deletion")
		}
	}
	usage, err := diskusage.New(config.GetCacheDir())
	if err != nil {
		return fmt.Errorf("cannot check Steam recovery disk space; refusing deletion: %w", err)
	}
	reserve := max(int64(c.opt.CacheMinFreeSpace), 256*1024*1024)
	if reserve > int64(usage.Available) || required > int64(usage.Available)-reserve {
		return fmt.Errorf("not enough free cache space to preserve dirty Steam data; refusing deletion")
	}
	root := filepath.Join(config.GetCacheDir(), "steam-data-recovery")
	if err = os.MkdirAll(root, 0700); err != nil {
		return fmt.Errorf("cannot create Steam data recovery folder; refusing deletion: %w", err)
	}
	dir, err := os.MkdirTemp(root, ".pending-")
	if err != nil {
		return err
	}
	complete := false
	defer func() {
		if !complete {
			// Only this newly allocated incomplete snapshot is removed. Never
			// remove the live cache or an earlier recovery snapshot on failure.
			_ = os.RemoveAll(dir)
		}
	}()
	source, err := file.OpenFile(c.toOSPath(name), os.O_RDONLY, 0600)
	if err != nil {
		return err
	}
	defer source.Close()
	if item.fd != nil {
		if err = item.fd.Sync(); err != nil {
			return err
		}
	}
	destination, err := file.OpenFile(filepath.Join(dir, "data.sparse"), os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return err
	}
	defer destination.Close()
	if err = file.SetSparse(destination); err != nil {
		return err
	}
	if err = destination.Truncate(info.Size); err != nil {
		return err
	}
	record := cloudSSDDeletedData{
		Version: 1, Path: name, Remote: c.fremote.Name() + ":" + c.fremote.Root(), SavedAt: time.Now().UTC(),
		Warning: "Only listed ranges are valid. Unlisted sparse holes are MISSING data. Do not overwrite a live cache with this file.",
		Info:    info, ValidRanges: make([]cloudSSDDeletedRange, 0, len(info.Rs)),
	}
	for _, r := range info.Rs {
		hash := sha256.New()
		written, copyErr := io.Copy(io.MultiWriter(io.NewOffsetWriter(destination, r.Pos), hash), io.NewSectionReader(source, r.Pos, r.Size))
		if copyErr != nil {
			return copyErr
		}
		if written != r.Size {
			return io.ErrUnexpectedEOF
		}
		record.ValidRanges = append(record.ValidRanges, cloudSSDDeletedRange{Range: r, SHA256: fmt.Sprintf("%x", hash.Sum(nil))})
	}
	if err = destination.Sync(); err != nil {
		return err
	}
	if err = destination.Close(); err != nil {
		return err
	}
	metadata, err := os.OpenFile(filepath.Join(dir, "recovery.json"), os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return err
	}
	err = json.NewEncoder(metadata).Encode(record)
	if err == nil {
		err = metadata.Sync()
	}
	if closeErr := metadata.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		return err
	}
	final := filepath.Join(root, "saved-"+strings.TrimPrefix(filepath.Base(dir), ".pending-"))
	if err = os.Rename(dir, final); err != nil {
		return err
	}
	complete = true
	fs.Logf(name, "Preserved %d dirty Steam cache bytes before deletion in %s", info.Rs.Size(), final)
	return nil
}
