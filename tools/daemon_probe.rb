require 'open3'
require 'json'

script = ARGV[0] || 'ruby/standalone_daemon.rb'
Dir.chdir(File.dirname(File.expand_path(script)))

Open3.popen3('ruby', File.basename(script)) do |stdin, stdout, stderr, wait|
  stderr_thread = Thread.new { stderr.read }

  stdin.puts({ command: 'ping', arguments: {}, request_id: 1 }.to_json)
  stdin.flush

  deadline = Time.now + 15
  lines = []
  while Time.now < deadline
    ready = IO.select([stdout], nil, nil, 1)
    if ready
      begin
        line = stdout.gets
        break if line.nil?
        lines << line.strip
        break if lines.any? { |l| l.include?('"type":"result"') || l.include?('"type" => "result"') }
      rescue EOFError
        break
      end
    end
    break if wait.alive? == false
  end

  puts "=== STDOUT (#{lines.size} frames) ==="
  lines.each { |l| puts l[0, 400] }

  Process.kill('KILL', wait.pid) rescue nil
  err = stderr_thread.value.to_s
  puts "=== STDERR ===" unless err.strip.empty?
  puts err[0, 1200] unless err.strip.empty?
  puts "=== done ==="
end